using static Unhinged.Native;
using static Unhinged.HeaderParsing;
using static Unhinged.ResponseBuilder;
using static Unhinged.ProcessorArchDependant;

namespace Unhinged;

// ReSharper disable once SuggestVarOrType_BuiltInTypes
// (var sucks for this type of project, I need to see the types)

internal static unsafe class Program
{
    // ===== HTTP response buffers (pinned) =====
    private static readonly byte[] _response200 = Build200();
    private static readonly GCHandle _h200 = GCHandle.Alloc(_response200, GCHandleType.Pinned);
    private static readonly byte* _p200 = (byte*)_h200.AddrOfPinnedObject();
    private static readonly int _len200 = _response200.Length;

    private static readonly byte[] _response431 = BuildSimpleResponse(431, "Request Header Fields Too Large");
    private static readonly GCHandle _h431 = GCHandle.Alloc(_response431, GCHandleType.Pinned);
    private static readonly byte* _p431 = (byte*)_h431.AddrOfPinnedObject();
    private static readonly int _len431 = _response431.Length;

    private const int Backlog = 16384;
    private const int MaxHeader = 16 * 1024;

    // ===== Entry point =====
    public static void Main(string[] args)
    {
        Console.WriteLine($"Arch={RuntimeInformation.ProcessArchitecture}, Packed={(Packed ? 12 : 16)}-byte epoll_event");

        int port = 8080;
        int workers = Math.Max(8, Math.Min(Environment.ProcessorCount / 2, 16)); // good start for -c512
        
        var (listenFd, acceptBlocking) = CreateListenSocket(port);

        // Spin up workers
        var W = new Worker[workers];
        for (int i = 0; i < workers; i++)
        {
            W[i] = new Worker(i, 512);
            int iCap = i;
            var t = new Thread(() => WorkerLoop(W[iCap])) { IsBackground = true, Name = $"worker-{iCap}" };
            t.Start();
        }

        // Single acceptor thread
        AcceptorLoop(listenFd, W);
    }

    // ===== Socket setup =====
    private static (int listenFd, bool blocking) CreateListenSocket(int port)
    {
        int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, IPPROTO_TCP);
        if (fd < 0) throw new Exception($"socket failed errno={Marshal.GetLastPInvokeError()}");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, ref one, sizeof(int));

        // Non-blocking
        int fl = fcntl(fd, F_GETFL, 0);
        if (fl >= 0) fcntl(fd, F_SETFL, fl | O_NONBLOCK);

        var addr = new sockaddr_in
        {
            sin_family = (ushort)AF_INET,
            sin_port = Htons((ushort)port),
            sin_addr = new in_addr { s_addr = 0 }, // 0.0.0.0
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
        // Optionally epoll on listen to avoid busy loop
        int ep = epoll_create1(EPOLL_CLOEXEC);
        if (ep < 0) throw new Exception("epoll_create1 (acceptor) failed");
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, EPOLLIN | EPOLLERR | EPOLLHUP, listenFd);
        if (epoll_ctl(ep, EPOLL_CTL_ADD, listenFd, (IntPtr)ev) != 0)
            throw new Exception("epoll_ctl ADD listen failed");

        IntPtr eventsBuf = Marshal.AllocHGlobal(EvSize * 128);

        for (;;)
        {
            int n = epoll_wait(ep, eventsBuf, 128, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; throw new Exception("epoll_wait acceptor"); }

            for (int i = 0; i < n; i++)
            {
                ReadEpollEvent((byte*)eventsBuf + i * EvSize, out uint events, out int fd);
                if (fd != listenFd) continue;
                if ((events & (EPOLLERR | EPOLLHUP)) != 0) continue;
                if ((events & EPOLLIN) == 0) continue;

                for (;;)
                {
                    int cfd = accept4(listenFd, IntPtr.Zero, IntPtr.Zero, SOCK_NONBLOCK | SOCK_CLOEXEC);
                    if (cfd >= 0)
                    {
                        int one = 1;
                        setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, ref one, sizeof(int));
                        var lg = new Linger { l_onoff = 0, l_linger = 0 };
                        setsockopt(cfd, SOL_SOCKET, SO_LINGER, ref lg, (uint)Marshal.SizeOf<Linger>());

                        int w = ChooseLeastBusy(workers);
                        workers[w].Inbox.Enqueue(cfd);
                        Interlocked.Increment(ref workers[w].Current);

                        // wake worker
                        ulong inc = 1;
                        write(workers[w].NotifyEfd, (IntPtr)(&inc), 8);
                        continue;
                    }

                    int err = Marshal.GetLastPInvokeError();
                    if (err == EINTR) continue;
                    if (err == EAGAIN || err == EWOULDBLOCK) break;
                    // transient accept error: break to next epoll tick
                    break;
                }
            }
        }
    }

    private static int ChooseLeastBusy(Worker[] workers)
    {
        int best = 0; long bestLoad = long.MaxValue;
        for (int i = 0; i < workers.Length; i++)
        {
            long load = Volatile.Read(ref workers[i].Current);
            if (load < bestLoad) { bestLoad = load; best = i; }
        }
        return best;
    }

    // ===== Worker loop =====
    private static void WorkerLoop(Worker W)
    {
        var conns = new Dictionary<int, Connection>(capacity: 1024);

        for (;;)
        {
            int n = epoll_wait(W.Ep, W.EventsBuf, W.MaxEvents, -1);
            if (n < 0) { if (Marshal.GetLastPInvokeError() == EINTR) continue; throw new Exception("epoll_wait worker"); }

            for (int i = 0; i < n; i++)
            {
                ReadEpollEvent((byte*)W.EventsBuf + i * EvSize, out uint evs, out int fd);

                if (fd == W.NotifyEfd)
                {
                    // drain eventfd and add pending fds
                    ulong tmp;
                    while (read(W.NotifyEfd, (IntPtr)(&tmp), 8) > 0) { }
                    while (W.Inbox.TryDequeue(out int cfd))
                    {
                        // register client fd
                        byte* ev = stackalloc byte[EvSize];
                        WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, cfd);
                        epoll_ctl(W.Ep, EPOLL_CTL_ADD, cfd, (IntPtr)ev);
                        conns[cfd] = new Connection { Fd = cfd };
                    }
                    continue;
                }

                // early close on error/hup
                if ((evs & (EPOLLERR | EPOLLHUP | EPOLLRDHUP)) != 0)
                {
                    CloseConn(fd, conns, W);
                    continue;
                }

                if ((evs & EPOLLIN) != 0)
                {
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }

                    // ensure space
                    int avail = c.Buf.Length - c.Tail;
                    if (avail == 0)
                    {
                        if (c.Head > 0) { c.CompactIfNeeded(); avail = c.Buf.Length - c.Tail; }
                        if (avail == 0)
                        {
                            int used = c.Tail - c.Head;
                            if (used >= MaxHeader)
                            {
                                // 431 then close
                                send(fd, (IntPtr)_p431, (ulong)_len431, MSG_NOSIGNAL);
                                CloseConn(fd, conns, W);
                                continue;
                            }
                            Array.Resize(ref c.Buf, Math.Min(Math.Max(c.Buf.Length * 2, c.Buf.Length + 1024), MaxHeader));
                            avail = c.Buf.Length - c.Tail;
                            if (avail == 0) continue;
                        }
                    }

                    for (;;)
                    {
                        long got;
                        fixed (byte* p = &c.Buf[c.Tail])
                            got = recv(fd, (IntPtr)p, (ulong)avail, 0);

                        if (got > 0)
                        {
                            c.Tail += (int)got;
                            if (TryServeBufferedRequests(c, fd, W, conns))
                                break; // armed EPOLLOUT, stop reading now

                            avail = c.Buf.Length - c.Tail;
                            if (avail == 0) break;
                            continue;
                        }
                        else if (got == 0) { CloseConn(fd, conns, W); break; }
                        else
                        {
                            int err = Marshal.GetLastPInvokeError();
                            if (err == EAGAIN || err == EWOULDBLOCK) break;
                            if (err == ECONNRESET || err == ECONNABORTED || err == EPIPE) { CloseConn(fd, conns, W); break; }
                            CloseConn(fd, conns, W); break;
                        }
                    }
                    continue;
                }

                if ((evs & EPOLLOUT) != 0)
                {
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }
                    if (!c.WantWrite)
                    {
                        // back to reads
                        byte* ev = stackalloc byte[EvSize];
                        WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                        epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
                        continue;
                    }

                    for (;;)
                    {
                        long nSent = send(fd, (IntPtr)(_p200 + c.RespSent), (ulong)(_len200 - c.RespSent), MSG_NOSIGNAL);
                        if (nSent > 0)
                        {
                            c.RespSent += (int)nSent;
                            if (c.RespSent == _len200)
                            {
                                c.WantWrite = false;
                                c.RespSent = 0;

                                // back to reads
                                byte* ev = stackalloc byte[EvSize];
                                WriteEpollEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                                epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);

                                // try serving any additional pipelined request already buffered
                                TryServeBufferedRequests(c, fd, W, conns);
                                break;
                            }
                            continue;
                        }
                        int err = (nSent == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
                        if (err == EAGAIN) break; // stay in EPOLLOUT
                        if (err == EPIPE || err == ECONNRESET) { CloseConn(fd, conns, W); break; }
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
        for (;;)
        {
            int idx = FindCrlfCrlf(c.Buf, c.Head, c.Tail);
            if (idx < 0) return false;
            c.Head = idx + 4;

            // try to send whole response now
            int sent = 0;
            for (;;)
            {
                long n = send(fd, (IntPtr)(_p200 + sent), (ulong)(_len200 - sent), MSG_NOSIGNAL);
                if (n > 0) { sent += (int)n; if (sent == _len200) break; continue; }
                int err = (n == 0) ? EAGAIN : Marshal.GetLastPInvokeError();
                if (err == EAGAIN)
                {
                    c.WantWrite = true;
                    c.RespSent = sent;
                    byte* ev = stackalloc byte[EvSize];
                    WriteEpollEvent(ev, EPOLLOUT | EPOLLRDHUP | EPOLLERR | EPOLLHUP, fd);
                    epoll_ctl(W.Ep, EPOLL_CTL_MOD, fd, (IntPtr)ev);
                    return true; // will resume in EPOLLOUT
                }
                // write error
                CloseConn(fd, conns, W);
                return true;
            }

            // fully sent → reset/compact
            if (c.Head >= c.Tail) { c.Head = c.Tail = 0; return false; }
            if (c.Head > 0) c.CompactIfNeeded();
            // loop to serve next request if already buffered
        }
    }

    // ===== Close helpers =====
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseConn(int fd, Dictionary<int, Connection> map, Worker W)
    {
        map.Remove(fd);
        CloseQuiet(fd);
        Interlocked.Decrement(ref W.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseQuiet(int fd) { try { close(fd); } catch { } }
}
