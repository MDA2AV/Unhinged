using static Unhinged.Native;
using static Unhinged.ProcessorArchDependant;

namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes

// First draft, will be reworked
internal sealed unsafe class Worker : IDisposable
{
    // Worker index (for logging, load balancing, etc.)
    internal readonly int Index;

    /* Dropped this, memory slabs are no longer indexable, using per connection pinned allocation
     
    internal readonly int MaxConnections;
    private readonly ulong[] ConnectionStates;
    
    public int GetFirstFreeConnectionIndex()
    {
        for (int i = 0; i < ConnectionStates.Length; i++)
        {
            ulong bits = ConnectionStates[i];
            
            if (bits == ulong.MaxValue) 
                continue; // there is at least one 0-bit
            
            ulong freeMask = ~bits;
            int bitIndex = BitOperations.TrailingZeroCount(freeMask); // index of first free in this block
            int index = (i << 6) + bitIndex;
            
            if (index >= MaxConnections) 
                continue;
            
            ConnectionStates[i] = bits | (1UL << bitIndex); // MARK USED
            return index;
        }
        return -1; // no free
    }

    public void Free(int index)
    {
        int block = index >> 6;
        int bit = index & 63;
        ConnectionStates[block] &= ~(1UL << bit);
    }

    public bool IsUsed(int index)
    {
        int block = index >> 6;
        int bit = index & 63;
        return ((ConnectionStates[block] >> bit) & 1UL) != 0;
    }
    */

    // The epoll file descriptor created by epoll_create1().
    // Each worker has its own epoll instance and waits on it in its own thread.
    internal readonly int Ep;

    // eventfd handle used to wake up this worker from other threads.
    // When another thread enqueues a connection for this worker, it writes to this eventfd.
    internal readonly int NotifyEfd;

    // Queue used by other threads to send (enqueue) new client socket fds to this worker.
    // The worker will drain this queue whenever it gets a wakeup from NotifyEfd.
    internal readonly ConcurrentQueue<int> Inbox = new();

    // Number of active (live) connections currently managed by this worker.
    // Used for load balancing — may be read by other threads, so should ideally
    // be updated atomically (Interlocked.Increment/Decrement).
    internal int Current;

    // Pointer to an unmanaged buffer large enough to hold all epoll_event structs
    // returned by epoll_wait(). This avoids allocating arrays on every call.
    internal readonly IntPtr EventsBuf;

    // Maximum number of events epoll_wait() should return in one batch.
    internal readonly int MaxEvents;

    /// <summary>
    /// Initializes a new epoll worker.
    /// Each worker:
    /// - owns its own epoll instance,
    /// - has an eventfd for thread-safe wakeups,
    /// - allocates an events buffer to read epoll_wait() results,
    /// - and can be notified via its NotifyEfd to adopt new connections.
    /// </summary>
    internal Worker(int idx, int maxEvents, int maxConnections = 64)
    {
        Index = idx;
        MaxEvents = maxEvents;
        
        //MaxConnections  = maxConnections;
        // All connections initialize as false (free)
        // This is a helper array to help attributing an index to each connection so that we can slice the byte*
        //ConnectionStates =  new ulong[(maxConnections + 63) / 64];

        // Create epoll instance with CLOEXEC flag (auto-close on exec).
        Ep = epoll_create1(EPOLL_CLOEXEC);
        if (Ep < 0)
            throw new Exception("epoll_create1 failed");

        // Create eventfd used to notify (wake) this worker.
        // EFD_NONBLOCK = non-blocking mode (read/write won't block)
        // EFD_CLOEXEC  = auto-close on exec
        NotifyEfd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (NotifyEfd < 0)
            throw new Exception("eventfd failed");

        // Prepare a single epoll_event structure on the stack.
        // It will be registered with EPOLLIN so epoll_wait wakes when NotifyEfd becomes readable.
        byte* ev = stackalloc byte[EvSize];

        // Fill the event struct with (EPOLLIN, NotifyEfd)
        // This means: "notify me when NotifyEfd has data to read"
        WriteEpollEvent(ev, EPOLLIN, NotifyEfd);

        // Register the eventfd with our epoll instance.
        // Now, any write() to NotifyEfd will wake up epoll_wait() on this worker.
        if (epoll_ctl(Ep, EPOLL_CTL_ADD, NotifyEfd, (IntPtr)ev) != 0)
            throw new Exception("epoll_ctl ADD notify failed");

        // Allocate unmanaged memory to hold all epoll_event results.
        // Each call to epoll_wait() will fill up to MaxEvents entries in this buffer.
        EventsBuf = Marshal.AllocHGlobal(EvSize * MaxEvents);
    }
    
    public void Dispose()
    {
        try { if (Ep >= 0) close(Ep); } catch { /* log */ }
        try { if (NotifyEfd >= 0) close(NotifyEfd); } catch { /* log */ }
        if (EventsBuf != IntPtr.Zero) Marshal.FreeHGlobal(EventsBuf);
        // Optionally GC.SuppressFinalize(this);
    }
}