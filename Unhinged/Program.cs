using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Unhinged.Native;
using static Unhinged.HeaderParsing;
using static Unhinged.ProcessorArchDependant;

namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
#pragma warning disable CA2014

[SkipLocalsInit] internal static unsafe class Program
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

    private const int Backlog = 16384; // listen() backlog hint to the kernel

    // ===== Entry point =====
    public static void Main(string[] args)
    {
        Console.WriteLine($"Arch={RuntimeInformation.ProcessArchitecture}, Packed={(Packed ? 12 : 16)}-byte epoll_event");

        const int port = 8080;
        // Choose a bounded number of workers (heuristic tuned for moderate -c values like 512)
        int workers = Math.Max(8, Math.Min(Environment.ProcessorCount / 2, 16)); // good start for -c512
        
        // Create and bind the listening socket (non-blocking)
        var (listenFd, acceptBlocking) = CreateListenSocket(port);

        // Spin up workers. Each worker owns an epoll instance and an eventfd notifier.
        var W = new Worker[workers];
        for (int i = 0; i < workers; i++)
        {
            W[i] = new Worker(i, 512, 64); // MaxEvents per epoll_wait for this worker
            int iCap = i; // capture loop variable safely
            var t = new Thread(() => WorkerLoop(W[iCap]), 10 * 1024 * 1024) // 10MB
            {
                IsBackground = true, Name = $"worker-{iCap}" 
            };
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
            int load = Volatile.Read(ref workers[i].Current);
            if (load < bestLoad) { bestLoad = load; best = i; }
        }
        return best;
    }
    
    private const int wrStackSize = 1024;
    private const int recStackSize = 1024 * 16;

    // ===== Worker loop =====
    private static void WorkerLoop(Worker W)
    {
        // Per-worker connection table
        var conns = new Dictionary<int, Connection>(capacity: W.MaxConnections);
        
        byte* sendBufferOrigin = stackalloc byte[1024 * W.MaxConnections];
        byte* readBufferOrigin = stackalloc byte[1024 * 16 * W.MaxConnections];
        
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
                        
                        // Adding a new connection to the pool, setting the file descriptor for the client socket 
                        // and the byte* pointing to the stack allocated write buffer segment
                        var connectionIndex = W.GetFirstFreeConnectionIndex();
                        conns[cfd] = new Connection
                        {
                            //Fd = cfd, 
                            WriteBuffer = new FixedBufferWriter(sendBufferOrigin + (connectionIndex * wrStackSize), wrStackSize)
                        };
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
                    
                    // If the receiving buffer has no space available, that means that either there are
                    // a lot of requests to be served, or a huge request is being received and is larger than the array size.
                    // We cannot resize the buffer for now, it is costly, implement it in the future
                    if (avail == 0)
                    {
                        throw new NotImplementedException("No available space in the receiving buffer");
                        
                        // Check if there are requests available to be handled
                        // TODO: Implement this

                        // If not, resize the receiving buffer (very difficult if dealing with stack allocated buffer)
                    }
                    
                    // Read as much as possible until EAGAIN or buffer is full.
                    // Read until EAGAIN (socket drained)
                    while (true)
                    {
                        long got;
                        fixed (byte* p = &c.Buf[c.Tail])
                            got = recv(fd, (IntPtr)p, (ulong)avail, 0);

                        if (got > 0)
                        {
                            c.Tail += (int)got;
                            continue;
                            
                        }
                        if (got == 0) { CloseConn(fd, conns, W); break; } // peer closed
                        
                        int err = Marshal.GetLastPInvokeError();
                        if (err is EAGAIN or EWOULDBLOCK)
                        {
                            // Nothing more to read (drained)
                            // Try to parse for complete requests
                            // TODO: Possibility of after handling all requests in buffer, try to read more data in this same loop?
                            // TODO: That could be an issue because this could give more airtime to a specific fd
                            // TODO: We want to release the loop to move into another fd's?
                            break;
                        } 
                        if (err is ECONNRESET or ECONNABORTED or EPIPE) { CloseConn(fd, conns, W); break; }
                        CloseConn(fd, conns, W); break; // default: close on unexpected errors
                    }
                    
                    // TODO: A potential improvement for high load could be just arm EPOLLOUT for this fd
                    // TODO: and continue; so that we can handle other fd's..
                    var dataToBeFlushedAvailable = TryParseRequests(c);
                    if (dataToBeFlushedAvailable)
                    {
                        var tryEmptyResult = TryEmptyWriteBuffer(c, ref fd);
                        if (tryEmptyResult == EmptyAttemptResult.Complete)
                        {
                            // All requests were flushed, stay EPOLLIN
                            // Move on to the next event
                            continue;
                        }
                        if (tryEmptyResult == EmptyAttemptResult.Incomplete)
                        {
                            // There is still data to be flushed in the buffer, arm EPOLLOUT
                            ArmEpollOut(ref fd, W.Ep);

                            continue;
                        }
                        if (tryEmptyResult == EmptyAttemptResult.CloseConnection)
                        {
                            CloseConn(fd, conns, W);
                            continue;
                        }
                    }
                    
                    // Move on to the next event...
                    continue;
                }

                // 4) Write-ready path
                if ((evs & EPOLLOUT) != 0)
                {
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }
                    
                    var tryEmptyResult = TryEmptyWriteBuffer(c, ref fd);
                    if (tryEmptyResult == EmptyAttemptResult.Complete)
                    {
                        // All requests were flushed, arm EPOLLIN
                        // Move on to the next event
                        ArmEpollIn(ref fd, W.Ep);
                        continue;
                    }
                    if (tryEmptyResult == EmptyAttemptResult.Incomplete)
                    {
                        // There is still data to be flushed in the buffer, stay EPOLLOUT
                        continue;
                    }
                    if (tryEmptyResult == EmptyAttemptResult.CloseConnection)
                    {
                        CloseConn(fd, conns, W);
                        continue;
                    }
                }
            }
        }
    }

    private static void ArmEpollIn(ref int fd, int ep)
    {
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
        epoll_ctl(ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
    }

    private static void ArmEpollOut(ref int fd, int ep)
    {
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, EPOLLOUT | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
        epoll_ctl(ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
    }
    
    // Handles requests and commits response to the write buffer, does not attempt to send(flush)
    // Returns flag signaling if there is data to be flushed in the writing buffer
    private static bool TryParseRequests(Connection connection)
    {
        bool hasDataToFlush = false;
        
        while (true)
        {
            // Try getting a full request header, if unsuccessful signal caller more data is needed
            int idx = FindCrlfCrlf(connection.Buf, connection.Head, connection.Tail);
            if (idx < 0)
                break;
            connection.Head = idx + 4; // advance past CRLFCRLF
            
            // A full request was received, handle it!
            CommitResponse(connection);

            hasDataToFlush = true;
        }
        
        // If there is unprocessed data in the receiving buffer (incomplete request) which is not at buffer start
        // Move the incomplete request to the buffer start and reset head and tail to 0
        if (connection.Head > 0 && connection.Head < connection.Tail)
        {
            Array.Copy(
                connection.Buf, 
                connection.Head, 
                connection.Buf, 
                0, 
                connection.Tail - connection.Head);
            // For byte* use Buffer.MemoryCopy
            //Buffer.MemoryCopy(connection.RecBuf + connection.RecHead, connection.RecBuf, recStackSize, connection.Tail - connection.RecHead);
        }
        
        //Reset the receiving buffer
        connection.Head = connection.Tail = 0;
        return hasDataToFlush;
    }

    private enum EmptyAttemptResult { Complete, Incomplete, CloseConnection }
    // Tries to empty the writing buffer until all data sent or EAGAIN
    // If all data was sent, back to EPOLLIN
    // If EAGAIN is hit, stay EPOLLOUT
    private static EmptyAttemptResult TryEmptyWriteBuffer(Connection connection, ref int fd)
    {
        while (true)
        {
            byte* headPointer = connection.WriteBuffer.Ptr + connection.WriteBuffer.Head;
            long remaining = (long)(connection.WriteBuffer.Tail - connection.WriteBuffer.Head);
            
            // TODO: Check if send can return negative values
            long n = send(fd, headPointer, remaining, MSG_NOSIGNAL);

            if (n > 0)
            {
                // Check if all data was sent
                if (n == remaining)
                {
                    // Remaining data was flushed/sent
                    
                    // Reset Writing buffer, point to the start
                    connection.WriteBuffer.Reset();
                    
                    // If all data was sent, signal caller
                    return EmptyAttemptResult.Complete;
                }
                
                // At least some data was flushed
                // Update the Head
                connection.WriteBuffer.Head += (int)n;
                
                // Some data was flushed but not all, try again (possibly until EAGAIN)
                continue;
            }
            
            // Wasn't able to flush, why?
            int err = (n == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
            if (err == EAGAIN)
            {
                // Notify the caller that we must keep trying to flush the data (arm EPOLLOUT)
                return EmptyAttemptResult.Incomplete;
            }
            
            // Other error, signal caller to close the connection
            return EmptyAttemptResult.CloseConnection;
        }
    }

    private static void CommitResponse(Connection connection)
    {
        //TODO: Parse route
        CommitPlainTextResponse(connection);
        //CommitJsonResponse(connection);
    }
    
    private static void CommitJsonResponse(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: application/json; charset=UTF-8\r\n"u8 +
                                              "Content-Length: 27\r\n"u8 +
                                              //"Connection: keep-alive\r\n"u8 +
                                              "\r\n"u8);
        
        var message = new JsonMessage { Message = "Hello, World!" };
        
        /*
        var writer = new SimpleJsonWriter(connection.WriteBuffer);
        
        writer.WriteStartObject();
        writer.WritePropertyName("Message");
        writer.WriteStringValue("Hello, World!");
        writer.WriteEndObject();
        */
        
        //var serialized = JsonSerializer.Serialize(message, SerializerContext.JsonMessage);
        //using var writer = new Utf8JsonWriter(connection.WriteBuffer);
        //JsonSerializer.Serialize(writer, message, SerializerContext.JsonMessage);
        //Console.WriteLine($"Writer hash: {connection.WriteBuffer.GetHash}");
        //Console.WriteLine($"Tail: {Volatile.Read(ref connection.Tail)}");
        
        /*using var stream = new UnmanagedMemoryStream(c.WrBuf, 1024, 1024, FileAccess.Write);
        using var writer = new Utf8JsonWriter(stream);
        JsonSerializer.Serialize(writer, message);
        return (int)stream.Position;*/
    }

    private static void CommitPlainTextResponse(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: text/plain\r\n"u8 +
                                              "Content-Length: 13\r\n\r\n"u8 +
                                              "Hello, World!"u8);
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