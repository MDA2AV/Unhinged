using static Unhinged.Native;
using static Unhinged.HeaderParsing;
using static Unhinged.ResponseBuilder;
using static Unhinged.ProcessorArchDependant;

namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

internal static unsafe class Program
{
    /*
     * ================================ OVERVIEW ==================================
     * This program implements a high‑performance HTTP/1.1 server using Linux epoll
     * from C# via P/Invoke. The design is split into:
     *   - A single Acceptor thread:
     *       * epoll-waits the listening socket for EPOLLIN
     *       * accept4() new connections in non-blocking mode
     *       * load-balances accepted sockets to Worker threads using a simple
     *         "least busy" heuristic
     *       * wakes the chosen Worker via an eventfd (NotifyEfd)
     *
     *   - N Worker threads (N = workers):
     *       * Each Worker owns its own epoll instance (W.Ep)
     *       * Maintains a connection map (fd -> Connection)
     *       * Handles read-ready (EPOLLIN) and write-ready (EPOLLOUT) events
     *       * Parses HTTP headers by scanning for CRLFCRLF (\r\n\r\n)
     *       * Sends a prebuilt 200 OK response (pinned in memory to avoid copies)
     *       * Handles backpressure: if send() would block, arms EPOLLOUT and
     *         resumes when the socket becomes writable.
     *
     * Pinned response buffers:
     *   Pre-constructed HTTP responses are pinned to avoid GC relocations and to
     *   enable passing stable pointers directly to send().
     *
     * Memory strategy per connection:
     *   * Each Connection holds a byte[] buffer with head/tail indices.
     *   * We compact or grow the buffer up to MaxHeader (16 KiB) as needed.
     *   * Once CRLFCRLF is found, we immediately attempt to write the response.
     *
     * Error handling:
     *   * On errors like EPOLLERR/EPOLLHUP/EPOLLRDHUP or EPIPE/ECONNRESET, we
     *     close the fd and decrement the Worker load counter.
     *
     * Concurrency model:
     *   * The acceptor serializes acceptance and handoff.
     *   * Workers run independently; a connection is owned by exactly one Worker.
     * ==========================================================================
     */

    // ===== HTTP response buffers (pinned) =====
    // Build and pin a success response so we can send via raw pointer without extra copies.
    private static readonly byte[] _response200 = Build200();
    private static readonly GCHandle _h200 = GCHandle.Alloc(_response200, GCHandleType.Pinned);
    private static readonly byte* _p200 = (byte*)_h200.AddrOfPinnedObject();
    private static readonly int _len200 = _response200.Length;

    // Build and pin a 431 response for oversized headers.
    private static readonly byte[] _response431 = BuildSimpleResponse(431, "Request Header Fields Too Large");
    private static readonly GCHandle _h431 = GCHandle.Alloc(_response431, GCHandleType.Pinned);
    private static readonly byte* _p431 = (byte*)_h431.AddrOfPinnedObject();
    private static readonly int _len431 = _response431.Length;

    private const int Backlog = 16384; // listen() backlog hint to the kernel
    private const int MaxHeader = 16 * 1024; // cap for header buffer growth per connection

    // ===== Entry point =====
    public static void Main(string[] args)
    {
        Console.WriteLine($"Arch={RuntimeInformation.ProcessArchitecture}, Packed={(Packed ? 12 : 16)}-byte epoll_event");

        int port = 8080;
        // Choose a bounded number of workers (heuristic tuned for moderate -c values like 512)
        int workers = Math.Max(8, Math.Min(Environment.ProcessorCount / 2, 16)); // good start for -c512
        
        // Create and bind the listening socket (non-blocking)
        var (listenFd, acceptBlocking) = CreateListenSocket(port);

        // Spin up workers. Each worker owns an epoll instance and an eventfd notifier.
        var W = new Worker[workers];
        for (int i = 0; i < workers; i++)
        {
            W[i] = new Worker(i, 512); // MaxEvents per epoll_wait for this worker
            int iCap = i; // capture loop variable safely
            var t = new Thread(() => WorkerLoop(W[iCap])) { IsBackground = true, Name = $"worker-{iCap}" };
            t.Start();
        }

        // Single acceptor thread (current thread): accepts and hands off connections.
        AcceptorLoop(listenFd, W);
    }

    // ===== Socket setup =====
    private static (int listenFd, bool blocking) CreateListenSocket(int port)
    {
        // Create a TCP socket with close-on-exec.
        int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, IPPROTO_TCP);
        if (fd < 0) throw new Exception($"socket failed errno={Marshal.GetLastPInvokeError()}");

        // Allow reusing the address to ease restarts.
        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, ref one, sizeof(int));

        // Put the listening socket in non-blocking mode.
        int fl = fcntl(fd, F_GETFL, 0);
        if (fl >= 0) fcntl(fd, F_SETFL, fl | O_NONBLOCK);

        // Bind 0.0.0.0:port
        var addr = new sockaddr_in
        {
            sin_family = (ushort)AF_INET,
            sin_port = Htons((ushort)port),
            sin_addr = new in_addr { s_addr = 0 }, // 0.0.0.0 (INADDR_ANY)
            sin_zero = new byte[8]
        };
        if (bind(fd, ref addr, (uint)Marshal.SizeOf<sockaddr_in>()) != 0)
            throw new Exception($"bind failed errno={Marshal.GetLastPInvokeError()}");
        if (listen(fd, Backlog) != 0)
            throw new Exception($"listen failed errno={Marshal.GetLastPInvokeError()}");

        return (fd, false);
    }

    // ===== Acceptor: accept + handoff to least-busy worker =====
    private static void AcceptorLoop(int listenFd, Worker[] workers)
    {
        // Use epoll to sleep the acceptor until the listening socket becomes readable.
        int ep = epoll_create1(EPOLL_CLOEXEC);
        if (ep < 0) throw new Exception("epoll_create1 (acceptor) failed");
        byte* ev = stackalloc byte[EvSize];
        // Monitor the listening fd for incoming connections and errors.
        WriteEpollEvent(ev, EPOLLIN | EPOLLERR | EPOLLHUP, listenFd);

        Console.WriteLine($"Configuring listening epoll for socket {listenFd}");
        
        if (epoll_ctl(ep, EPOLL_CTL_ADD, listenFd, (IntPtr)ev) != 0)
            throw new Exception("epoll_ctl ADD listen failed");

        // Buffer to receive epoll events (capacity for 128 events per wait call).
        // NOTE: 128 is an operational batch size, not an OS limit. Increase if needed.
        IntPtr eventsBuf = Marshal.AllocHGlobal(EvSize * 128);

        for (;;)
        {
            // Block until at least one event is available on the listening epoll set.
            int n = epoll_wait(ep, eventsBuf, 128, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; throw new Exception("epoll_wait acceptor"); }

            for (int i = 0; i < n; i++)
            {
                ReadEpollEvent((byte*)eventsBuf + i * EvSize, out uint events, out int fd);
                if (fd != listenFd) continue;               // We only expect the listen fd on the acceptor set
                if ((events & (EPOLLERR | EPOLLHUP)) != 0) continue; // Skip if error/hangup on listen fd
                if ((events & EPOLLIN) == 0) continue;      // Only proceed on EPOLLIN

                // Drain accepts until EAGAIN: batched acceptance to reduce wakeups.
                for (;;)
                {
                    int cfd = accept4(listenFd, IntPtr.Zero, IntPtr.Zero, SOCK_NONBLOCK | SOCK_CLOEXEC);
                    if (cfd >= 0)
                    {
                        // Tweak connection-level options for latency and fast close.
                        int one = 1;
                        setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, ref one, sizeof(int));
                        var lg = new Linger { l_onoff = 0, l_linger = 0 }; // disable linger
                        setsockopt(cfd, SOL_SOCKET, SO_LINGER, ref lg, (uint)Marshal.SizeOf<Linger>());

                        // Choose the least-busy worker by Current (approximate in-flight count)
                        int w = ChooseLeastBusy(workers);
                        workers[w].Inbox.Enqueue(cfd);            // hand off fd to worker queue
                        Interlocked.Increment(ref workers[w].Current); // bump worker load metric
                        Console.WriteLine($"Incremented {workers[w].Ep} with cfg {cfd} to {workers[w].Current}");

                        // Wake the worker via eventfd. We write 8 bytes (uint64).
                        ulong inc = 1;
                        write(workers[w].NotifyEfd, (IntPtr)(&inc), 8);
                        continue; // continue draining accepts in this epoll tick
                    }

                    int err = Marshal.GetLastPInvokeError();
                    if (err == EINTR) continue;                  // retry
                    if (err == EAGAIN || err == EWOULDBLOCK) break; // no more to accept right now
                    // On transient or unexpected accept error, break to next epoll tick.
                    break;
                }
            }
        }
    }

    // Pick the worker with the lowest Current count.
    private static int ChooseLeastBusy(Worker[] workers)
    {
        int best = 0; int bestLoad = int.MaxValue;
        for (int i = 0; i < workers.Length; i++)
        {
            // Volatile: 
            int load = Volatile.Read(ref workers[i].Current);
            if (load < bestLoad) { bestLoad = load; best = i; }
        }
        return best;
    }

    // ===== Worker loop =====
    private static void WorkerLoop(Worker W)
    {
        // Per-worker connection table
        var conns = new Dictionary<int, Connection>(capacity: 1024);

        for (;;)
        {
            // Wait for I/O events or a wakeup from the acceptor (via NotifyEfd)
            int n = epoll_wait(W.Ep, W.EventsBuf, W.MaxEvents, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; throw new Exception("epoll_wait worker"); }

            for (int i = 0; i < n; i++)
            {
                ReadEpollEvent((byte*)W.EventsBuf + i * EvSize, out uint evs, out int fd);

                // 1) Notification path: new fds from acceptor via eventfd
                if (fd == W.NotifyEfd)
                {
                    // Drain the eventfd counter (consume all 64-bit values written).
                    ulong tmp;
                    while (read(W.NotifyEfd, (IntPtr)(&tmp), 8) > 0) { }
                    // Pull all pending client fds from the inbox and register with epoll
                    while (W.Inbox.TryDequeue(out int cfd))
                    {
                        // We care about readable input and remote half-close; errors/hups too.
                        byte* ev = stackalloc byte[EvSize];
                        WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, cfd);
                        epoll_ctl(W.Ep, EPOLL_CTL_ADD, cfd, (IntPtr)ev);
                        conns[cfd] = new Connection { Fd = cfd };
                    }
                    continue;
                }

                // 2) Early close on error/hup conditions
                if ((evs & (EPOLLERR | EPOLLHUP | EPOLLRDHUP)) != 0)
                {
                    CloseConn(fd, conns, W);
                    continue;
                }

                // 3) Read-ready path
                if ((evs & EPOLLIN) != 0)
                {
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }

                    // Ensure free space at the tail; compact or grow if necessary.
                    // TODO: This logic needs rework, doesn't sense
                    int avail = c.Buf.Length - c.Tail;
                    if (avail == 0)
                    {
                        if (c.Head > 0) { c.CompactIfNeeded(); avail = c.Buf.Length - c.Tail; }
                        if (avail == 0)
                        {
                            int used = c.Tail - c.Head;
                            if (used >= MaxHeader)
                            {
                                // Headers too large → send 431 and close.
                                send(fd, (IntPtr)_p431, (ulong)_len431, MSG_NOSIGNAL);
                                CloseConn(fd, conns, W);
                                continue;
                            }
                            // Grow buffer but never exceed MaxHeader.
                            Array.Resize(ref c.Buf, Math.Min(Math.Max(c.Buf.Length * 2, c.Buf.Length + 1024), MaxHeader));
                            avail = c.Buf.Length - c.Tail;
                            if (avail == 0) continue; // no space even after resize; bail out for now.
                        }
                    }

                    // Read as much as possible until EAGAIN or buffer is full.
                    for (;;)
                    {
                        long got;
                        fixed (byte* p = &c.Buf[c.Tail])
                            got = recv(fd, (IntPtr)p, (ulong)avail, 0);

                        if (got > 0)
                        {
                            c.Tail += (int)got;
                            // Try to parse and respond to any complete requests already buffered.
                            if (TryServeBufferedRequests(c, fd, W, conns))
                                break; // Response requires EPOLLOUT; stop reading now.

                            // If there is still room, continue reading in this loop.
                            avail = c.Buf.Length - c.Tail;
                            if (avail == 0) break;
                            continue;
                        }
                        else if (got == 0) { CloseConn(fd, conns, W); break; } // peer closed
                        else
                        {
                            int err = Marshal.GetLastPInvokeError();
                            if (err == EAGAIN || err == EWOULDBLOCK) break; // socket would block
                            if (err == ECONNRESET || err == ECONNABORTED || err == EPIPE) { CloseConn(fd, conns, W); break; }
                            CloseConn(fd, conns, W); break; // default: close on unexpected errors
                        }
                    }
                    continue;
                }

                // 4) Write-ready path
                if ((evs & EPOLLOUT) != 0)
                {
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }
                    if (!c.WantWrite)
                    {
                        // No pending write: go back to read mode.
                        byte* ev = stackalloc byte[EvSize];
                        WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                        epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
                        continue;
                    }

                    // Attempt to flush the remainder of the response.
                    for (;;)
                    {
                        long nSent = send(fd, (IntPtr)(_p200 + c.RespSent), (ulong)(_len200 - c.RespSent), MSG_NOSIGNAL);
                        if (nSent > 0)
                        {
                            c.RespSent += (int)nSent;
                            if (c.RespSent == _len200)
                            {
                                // Response fully flushed. Switch back to EPOLLIN.
                                c.WantWrite = false;
                                c.RespSent = 0;

                                byte* ev = stackalloc byte[EvSize];
                                WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                                epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);

                                // If there are more pipelined requests in the buffer, serve them.
                                TryServeBufferedRequests(c, fd, W, conns);
                                break;
                            }
                            continue; // still have bytes to send; loop
                        }
                        int err = (nSent == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
                        if (err == EAGAIN) break; // stay armed for EPOLLOUT
                        if (err is EPIPE or ECONNRESET) { CloseConn(fd, conns, W); break; }
                        CloseConn(fd, conns, W); break;
                    }
                    continue;
                }
            }
        }
    }

    // ===== HTTP request parse + send (CRLFCRLF) =====
    private static bool TryServeBufferedRequests(Connection c, int fd, Worker W, Dictionary<int, Connection> conns)
    {
        // Process as many complete requests as are already buffered (HTTP pipelining).
        for (;;)
        {
            // Look for the end of headers (\r\n\r\n). Returns index or -1 if not found.
            int idx = FindCrlfCrlf(c.Buf, c.Head, c.Tail);
            if (idx < 0) return false; // need more data
            c.Head = idx + 4;          // advance past CRLFCRLF
            
            // Build the response

            // Try to send the entire prebuilt 200 OK response immediately.
            int sent = 0;
            for (;;)
            {
                long n = send(fd, (IntPtr)(_p200 + sent), (ulong)(_len200 - sent), MSG_NOSIGNAL);
                if (n > 0)
                {
                    sent += (int)n; 
                    if (sent == _len200) 
                        break; 
                    
                    continue; 
                    
                }
                int err = (n == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
                if (err == EAGAIN)
                {
                    // Socket not currently writable → remember offset and arm EPOLLOUT
                    c.WantWrite = true;
                    c.RespSent = sent;
                    byte* ev = stackalloc byte[EvSize];
                    WriteEpollEvent(ev, EPOLLOUT | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                    epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
                    return true; // will resume in the EPOLLOUT path
                }
                
                // On any other error, close and report as handled.
                CloseConn(fd, conns, W);
                return true;
            }

            // If we reach here, the response was fully sent synchronously.
            // Reset/compact the buffer for potential additional pipelined requests.
            if (c.Head >= c.Tail) { c.Head = c.Tail = 0; return false; }
            if (c.Head > 0) c.CompactIfNeeded();
            // Loop to handle any next pipelined request already in the buffer.
        }
    }

    private static void CreateResponse()
    {
        
    }

    // ===== Close helpers =====
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseConn(int fd, Dictionary<int, Connection> map, Worker W)
    {
        // Remove from map, close fd, and decrement the worker's load counter.
        map.Remove(fd);
        CloseQuiet(fd);
        Interlocked.Decrement(ref W.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseQuiet(int fd) { try { close(fd); } catch { } }
}
