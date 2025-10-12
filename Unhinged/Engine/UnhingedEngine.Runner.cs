// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

public sealed partial class UnhingedEngine
{
    public void Run()
    {
        // Spin up workers. Each worker owns an epoll instance and an eventfd notifier.
        var W = new Worker[_nWorkers];
        for (int i = 0; i < _nWorkers; i++)
        {
            W[i] = new Worker(i, _maxEventsPerWake); // MaxEvents per epoll_wait for this worker
            int iCap = i; // capture loop variable safely
            var t = new Thread(() => WorkerLoop(W[iCap]), _maxStackSizePerThread) // 1MB
            {
                IsBackground = true, Name = $"worker-{iCap}" 
            };
            t.Start();
        }
        
        AcceptorLoop(_listenFd, W);
    }
}