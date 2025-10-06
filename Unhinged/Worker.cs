using static Unhinged.Native;
using static Unhinged.ProcessorArchDependant;

namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes

// First draft, will be reworked
internal sealed unsafe class Worker
{
    internal readonly int Index;
    internal readonly int Ep;
    internal readonly int NotifyEfd;
    internal readonly ConcurrentQueue<int> Inbox = new();
    internal long Current; // live conns
    internal readonly IntPtr EventsBuf;
    internal readonly int MaxEvents;

    internal Worker(int idx, int maxEvents)
    {
        Index = idx; MaxEvents = maxEvents;
        Ep = epoll_create1(EPOLL_CLOEXEC);
        
        if (Ep < 0) 
            throw new Exception("epoll_create1 failed");
        
        NotifyEfd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        
        if (NotifyEfd < 0) 
            throw new Exception("eventfd failed");

        // register notify efd
        byte* ev = stackalloc byte[EvSize];
        WriteEpollEvent(ev, EPOLLIN, NotifyEfd);
        
        if (epoll_ctl(Ep, EPOLL_CTL_ADD, NotifyEfd, (IntPtr)ev) != 0)
            throw new Exception("epoll_ctl ADD notify failed");

        EventsBuf = Marshal.AllocHGlobal(EvSize * MaxEvents);
    }
}