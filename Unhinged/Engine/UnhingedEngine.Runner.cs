// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

public sealed partial class UnhingedEngine
{
    /// <summary>
    /// Boots the engine:
    ///   1) Spawns N worker threads. Each worker owns:
    ///        - its own epoll instance (for client fds)
    ///        - an eventfd (for acceptor → worker wakeups)
    ///        - a bounded event batch size (_maxEventsPerWake)
    ///   2) Enters the acceptor loop, which:
    ///        - epoll-waits on the listening socket
    ///        - accept4()’s incoming connections in batches
    ///        - picks the least-busy worker and enqueues the new fd
    ///        - signals the worker via eventfd
    ///
    /// This method blocks indefinitely inside <see cref="AcceptorLoop"/> under normal operation.
    /// </summary>
    public void Run()
    {
        // Spin up workers. Each worker owns an epoll instance and an eventfd notifier.
        var W = new Worker[_nWorkers];

        for (int i = 0; i < _nWorkers; i++)
        {
            // Create the worker with its per-thread event capacity (batch size per epoll_wait).
            W[i] = new Worker(i, _maxEventsPerWake); // MaxEvents per epoll_wait for this worker

            // Capture the loop variable by value to avoid closure-capture bugs.
            int iCap = i; // capture loop variable safely

            // Start a dedicated OS thread for this worker.
            // - IsBackground=true so the process can exit if only workers remain.
            // - Name aids debugging and logs.
            // - Stack size is configurable to accommodate stackalloc-heavy hot paths.
            var t = new Thread(() => WorkerLoop(W[iCap]), _maxStackSizePerThread) // 1MB
            {
                IsBackground = true,
                Name = $"worker-{iCap}"
            };
            t.Start();
        }

        // The acceptor thread runs on the calling thread.
        // It never returns unless a fatal error occurs; it distributes work to W via eventfd.
        AcceptorLoop(_listenFd, W);
    }
}