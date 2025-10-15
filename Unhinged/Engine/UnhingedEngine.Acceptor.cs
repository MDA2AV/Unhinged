// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

[SkipLocalsInit] 
public sealed unsafe partial class UnhingedEngine
{
    /// <summary>
    /// The main acceptor loop responsible for:
    ///   - Waiting for readiness events on the listening socket via epoll
    ///   - Accepting new incoming TCP connections in batches (until EAGAIN)
    ///   - Assigning accepted sockets to the least-busy worker thread
    ///   - Waking workers via their dedicated eventfd notifications
    ///
    /// This loop runs continuously and never returns under normal operation.
    /// It operates in non-blocking mode to avoid stalling the acceptor thread.
    /// </summary>
    /// <param name="listenFd">File descriptor of the listening TCP socket</param>
    /// <param name="workers">Array of worker instances for load distribution</param>
    private static void AcceptorLoop(int listenFd, Worker[] workers)
    {
        // Use epoll to sleep the acceptor until the listening socket becomes readable.
        int ep = epoll_create1(EPOLL_CLOEXEC);
        if (ep < 0) 
            throw new Exception("epoll_create1 (acceptor) failed");
        byte* ev = stackalloc byte[EvSize];
        
        // Monitor the listening fd for incoming connections and errors.
        WriteEpollEvent(ev, EPOLLIN | EPOLLERR | EPOLLHUP, listenFd);

        Console.WriteLine($"Configuring listening epoll for socket {listenFd}");
        
        if (epoll_ctl(ep, EPOLL_CTL_ADD, listenFd, (IntPtr)ev) != 0)
            throw new Exception("epoll_ctl ADD listen failed");

        // Buffer to receive epoll events (capacity for MaxEventsPerWake events per wait call).
        // NOTE: MaxEventsPerWake is an operational batch size, not an OS limit. Increase if needed.
        IntPtr eventsBuf = Marshal.AllocHGlobal(EvSize * _maxEventsPerWake);

        // =========================== MAIN LOOP ==============================
        for (;;)
        {
            // Block until at least one event is available on the listening epoll set.
            int n = epoll_wait(ep, eventsBuf, _maxEventsPerWake, -1);
            if (n < 0)
            {
                // Retry if interrupted by a signal (EINTR)
                if (Marshal.GetLastPInvokeError() == EINTR) 
                    continue; 
                throw new Exception("epoll_wait acceptor"); 
            }

            // Iterate over each triggered event in this epoll tick.
            for (int i = 0; i < n; i++)
            {
                // Decode the i-th epoll_event structure
                ReadEpollEvent((byte*)eventsBuf + i * EvSize, out uint events, out int fd);
                
                if (fd != listenFd)                             // We only expect the listen fd on the acceptor set
                    continue;               
                
                if ((events & (EPOLLERR | EPOLLHUP)) != 0)      // Skip if error/hangup on listen fd
                    continue; 
                
                if ((events & EPOLLIN) == 0)                    // Only proceed on EPOLLIN
                    continue;

                // ===================== ACCEPT LOOP ============================
                // Drain accepts until EAGAIN: batched acceptance to reduce wakeups.
                for (;;)
                {
                    int cfd = accept4(listenFd, IntPtr.Zero, IntPtr.Zero, SOCK_NONBLOCK | SOCK_CLOEXEC);
                    if (cfd >= 0)
                    {
                        // Tweak connection-level options for latency and fast close.
                        int one = 1;
                        setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, ref one, sizeof(int));
                        var lg = new Linger { l_onoff = 0, l_linger = 0 }; // Disable linger to ensure fast close
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

                    // Handle transient accept errors
                    int err = Marshal.GetLastPInvokeError();
                    if (err == EINTR) continue;                  // Retry if interrupted
                    if (err is EAGAIN or EWOULDBLOCK) break;     // No more pending accepts
                    // On transient or unexpected accept error, break to next epoll tick.
                    break;
                }
            }
        }
    }
    
    /// <summary>
    /// Selects the worker with the smallest "Current" load counter.
    /// This is a simple O(N) heuristic suitable for small worker counts.
    /// </summary>
    private static int ChooseLeastBusy(Worker[] workers)
    {
        int best = 0; int bestLoad = int.MaxValue;
        for (int i = 0; i < workers.Length; i++)
        {
            int load = Volatile.Read(ref workers[i].Current);
            if (load < bestLoad)
            {
                bestLoad = load; best = i; 
            }
        }
        return best;
    }
}