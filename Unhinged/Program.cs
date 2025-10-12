using System.Text.Json;

namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
#pragma warning disable CA2014

internal class Program
{
    public static void Main(string[] args)
    {
        var builder = UnhingedEngine.CreateBuilder();
        
        var engine = builder.Build();
        
        engine.Run();
    }
    
    [ThreadStatic] private static Utf8JsonWriter? t_utf8JsonWriter;
    private static readonly JsonContext SerializerContext = JsonContext.Default;
    private static void CommitJsonResponse(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: application/json; charset=UTF-8\r\n"u8 +
                                              "Content-Length: 27\r\n"u8);
        connection.WriteBuffer.WriteUnmanaged(DateHelper.HeaderBytes);
        
        t_utf8JsonWriter ??= new Utf8JsonWriter(connection.WriteBuffer, new JsonWriterOptions { SkipValidation = true });
        t_utf8JsonWriter.Reset(connection.WriteBuffer);
        
        // Creating(Allocating) a new JsonMessage every request
        var message = new JsonMessage { Message = "Hello, World!" };
        // Serializing it every request
        JsonSerializer.Serialize(t_utf8JsonWriter, message, SerializerContext.JsonMessage);
    }

    private static void CommitPlainTextResponse(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: text/plain\r\n"u8 +
                                              "Content-Length: 13\r\n"u8);
        connection.WriteBuffer.WriteUnmanaged(DateHelper.HeaderBytes);
        connection.WriteBuffer.Write("Hello, World!"u8);
    }
}

/*
[SkipLocalsInit] internal static unsafe class Program1
{
    private const int Backlog = 16384; // listen() backlog hint to the kernel
    private const int MaxNumberConnectionsPerWorker = 1024;
    private const int InSlabSize = 16 * 1024;
    private const int OutSlabSize = 16 * 1024;
    private const int MaxEventsPerWake = 16;
    private const int MaxStackSizePerThread = 1024 * 1024;

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
            W[i] = new Worker(i, MaxEventsPerWake, MaxNumberConnectionsPerWorker); // MaxEvents per epoll_wait for this worker
            int iCap = i; // capture loop variable safely
            var t = new Thread(() => WorkerLoop(W[iCap]), MaxStackSizePerThread) // 1MB
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

        // Buffer to receive epoll events (capacity for MaxEventsPerWake events per wait call).
        // NOTE: MaxEventsPerWake is an operational batch size, not an OS limit. Increase if needed.
        IntPtr eventsBuf = Marshal.AllocHGlobal(EvSize * MaxEventsPerWake);

        for (;;)
        {
            // Block until at least one event is available on the listening epoll set.
            int n = epoll_wait(ep, eventsBuf, MaxEventsPerWake, -1);
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
                        //Console.WriteLine($"Incremented {workers[w].Ep} with cfg {cfd} to {workers[w].Current}");

                        // Wake the worker via eventfd. We write 8 bytes (uint64).
                        ulong inc = 1;
                        write(workers[w].NotifyEfd, (IntPtr)(&inc), 8);
                        continue; // continue draining accepts in this epoll tick
                    }

                    int err = Marshal.GetLastPInvokeError();
                    if (err == EINTR) continue;                  // retry
                    if (err is EAGAIN or EWOULDBLOCK) break; // no more to accept right now
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
    
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024*32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection>
    {
        public override Connection Create() => new(MaxNumberConnectionsPerWorker, InSlabSize, OutSlabSize);
        public override bool Return(Connection context)
        {
            // Potentially reset buffers here.
            return true;
        }
    }

    // ===== Worker loop =====
    private static void WorkerLoop(Worker W)
    {
        // Per-worker connection table
        var conns = new Dictionary<int, Connection>(capacity: MaxNumberConnectionsPerWorker);
        
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
                        conns[cfd] = ConnectionPool.Get();
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
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd, conns); continue; }

                    // Ensure free space at the tail; compact or grow if necessary.
                    // TODO: This logic needs rework, doesn't sense
                    //int avail = c.Buf.Length - c.Tail;
                    int avail = InSlabSize - c.Tail;
                    
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
                        //fixed (byte* p = &c.Buf[c.Tail])
                        //    got = recv(fd, (IntPtr)p, (ulong)avail, 0);
                        got = recv(fd, c.ReceiveBuffer, (ulong)avail, 0);

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
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd, conns); continue; }
                    
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
    
    private static bool TryParseRequests(Connection connection)
    {
        bool hasDataToFlush = false;
        
        while (true)
        {
            // Try getting a full request header, if unsuccessful signal caller more data is needed
            //int idx = FindCrlfCrlf(connection.Buf, connection.Head, connection.Tail);
            int idx = FindCrlfCrlf(connection.ReceiveBuffer, connection.Head, connection.Tail);
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
            Buffer.MemoryCopy(
                connection.ReceiveBuffer + connection.Head, 
                connection.ReceiveBuffer, 
                InSlabSize, 
                connection.Tail - connection.Head);
        }
        
        //Reset the receiving buffer
        connection.Head = connection.Tail = 0;
        return hasDataToFlush;
    }

    private enum EmptyAttemptResult { Complete, Incomplete, CloseConnection }

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

    [ThreadStatic] private static Utf8JsonWriter? t_utf8JsonWriter;
    private static readonly JsonContext SerializerContext = JsonContext.Default;
    private static void CommitJsonResponse(Connection connection)
    {
        connection.WriteBuffer.Write("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: application/json; charset=UTF-8\r\n"u8 +
                                              "Content-Length: 27\r\n"u8);
        connection.WriteBuffer.Write(DateHelper.HeaderBytes);
        
        t_utf8JsonWriter ??= new Utf8JsonWriter(connection.WriteBuffer, new JsonWriterOptions { SkipValidation = true });
        t_utf8JsonWriter.Reset(connection.WriteBuffer);
        
        var message = new JsonMessage { Message = "Hello, World!" };
        JsonSerializer.Serialize(t_utf8JsonWriter, message, SerializerContext.JsonMessage);
    }

    private static void CommitPlainTextResponse(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: text/plain\r\n"u8 +
                                              "Content-Length: 13\r\n"u8);
        connection.WriteBuffer.WriteUnmanaged(DateHelper.HeaderBytes);
        connection.WriteBuffer.Write("Hello, World!"u8);
    }
    
    // ===== Close helpers =====
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseConn(int fd, Dictionary<int, Connection> map, Worker W)
    {
        // Remove from map, close fd, and decrement the worker's load counter.
        ConnectionPool.Return(map[fd]);
        map.Remove(fd);
        CloseQuiet(fd, map);
        Interlocked.Decrement(ref W.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseQuiet(int fd, Dictionary<int, Connection> map) { try { close(fd); } catch { } }
}
*/