// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

public sealed unsafe partial class UnhingedEngine
{
    private enum EmptyAttemptResult { Complete, Incomplete, CloseConnection }
    
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024*32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection>
    {
        public override Connection Create() => new(_maxNumberConnectionsPerWorker, _inSlabSize, _outSlabSize);
        public override bool Return(Connection context)
        {
            // Potentially reset buffers here.
            return true;
        }
    }
    
    private static void WorkerLoop(Worker W)
    {
        // Per-worker connection table
        var conns = new Dictionary<int, Connection>(capacity: _maxNumberConnectionsPerWorker);
        
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
                    if (!conns.TryGetValue(fd, out var c)) { CloseQuiet(fd); continue; }

                    // Ensure free space at the tail; compact or grow if necessary.
                    // TODO: This logic needs rework, doesn't sense
                    //int avail = c.Buf.Length - c.Tail;
                    int avail = _inSlabSize - c.Tail;
                    
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
            
            // Find route here, before advancing the Head
            
            connection.Head = idx + 4; // advance past CRLFCRLF
            
            // A full request was received, handle it!
            _sRequestHandler(connection);

            hasDataToFlush = true;
        }
        
        // If there is unprocessed data in the receiving buffer (incomplete request) which is not at buffer start
        // Move the incomplete request to the buffer start and reset head and tail to 0
        if (connection.Head > 0 && connection.Head < connection.Tail)
        {
            Buffer.MemoryCopy(
                connection.ReceiveBuffer + connection.Head, 
                connection.ReceiveBuffer, 
                _inSlabSize, 
                connection.Tail - connection.Head);
        }
        
        //Reset the receiving buffer
        connection.Head = connection.Tail = 0;
        return hasDataToFlush;
    }
    
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
    
    // ===== Close helpers =====
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseConn(int fd, Dictionary<int, Connection> map, Worker W)
    {
        // Remove from map, close fd, and decrement the worker's load counter.
        ConnectionPool.Return(map[fd]);
        map.Remove(fd);
        CloseQuiet(fd);
        Interlocked.Decrement(ref W.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseQuiet(int fd) { try { close(fd); } catch { } }
}