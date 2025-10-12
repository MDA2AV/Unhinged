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
    /// Factory for configuring and constructing an <see cref="UnhingedEngine"/>.
    /// Usage:
    /// <code>
    /// var engine = UnhingedEngine.CreateBuilder()
    ///     .SetPort(8080)
    ///     .SetBacklog(16384)
    ///     .SetMaxNumberConnectionsPerWorker(512)
    ///     .SetSlabSizes(16 * 1024, 16 * 1024)
    ///     .SetMaxEventsPerWake(512)
    ///     .SetMaxStackSizePerThread(1024 * 1024)
    ///     .SetNWorkersSolver(() => Environment.ProcessorCount / 2)
    ///     .InjectRequestHandler(conn => { /* write response */ })
    ///     .Build();
    /// </code>
    /// </summary>
    public static UnhingedBuilder CreateBuilder() => new UnhingedBuilder();

    // ===== Engine-wide configuration (static for now; set via builder) =====
    private static int _port = 8080;
    private static int _backlog = 16384;
    private static int _maxNumberConnectionsPerWorker = 512;
    private static int _inSlabSize = 16 * 1024;
    private static int _outSlabSize = 16 * 1024;
    private static int _maxEventsPerWake  = 512;
    private static int _maxStackSizePerThread  = 1024 * 1024;
    private static int _listenFd;
    private static int _acceptBlocking;
    private static int _nWorkers;
    
    private static Func<int>? _calculateNumberWorkers;

    // Default request handler (overridden via builder). Writes a minimal plaintext response.
    private static Action<Connection> _sRequestHandler = DefaultRequestHandler;

    private static void DefaultRequestHandler(Connection connection)
    {
        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Server: W\r\n"u8 +
                                              "Content-Type: text/plain\r\n"u8 +
                                              "Content-Length: 28\r\n\r\n"u8 +
                                              "Request handler was not set!"u8 );
    }
    
    private UnhingedEngine() { }
    
    /// <summary>
    /// Fluent builder for <see cref="UnhingedEngine"/>. This configures static fields,
    /// then <see cref="Build"/> finalizes OS resources (listen socket) and decides worker count.
    /// </summary>
    public sealed class UnhingedBuilder
    {
        private readonly UnhingedEngine _engine;
        public UnhingedBuilder() => _engine = new UnhingedEngine();

        /// <summary>Set the TCP listening port (default: 8080).</summary>
        public UnhingedBuilder SetPort(int port)
        {
            _port = port;
            return this;
        }

        /// <summary>
        /// Set the listen backlog (default: 16384). This is a hint to the kernel for
        /// the maximum pending connection queue length.
        /// </summary>
        public UnhingedBuilder SetBacklog(int backlog)
        {
            _backlog = backlog;
            return this;
        }

        /// <summary>
        /// Max concurrent connections each worker is allowed to track (default: 512).
        /// Used for per-worker capacity planning and slab sizing.
        /// </summary>
        public UnhingedBuilder SetMaxNumberConnectionsPerWorker(int maxNumberConnectionsPerWorker)
        {
            _maxNumberConnectionsPerWorker = maxNumberConnectionsPerWorker;
            return this;
        }

        /// <summary>
        /// Configure input/output slab sizes used by connections (defaults: 16 KiB each).
        /// </summary>
        public UnhingedBuilder SetSlabSizes(int inSlabSize, int outSlabSize)
        {
            _inSlabSize = inSlabSize;
            _outSlabSize = outSlabSize;
            return this;
        }

        /// <summary>
        /// Operational batch size for epoll wait loops—how many events to pull per wake (default: 512).
        /// Increase if workers often see saturated event bursts.
        /// </summary>
        public UnhingedBuilder SetMaxEventsPerWake(int maxEventsPerWake)
        {
            _maxEventsPerWake = maxEventsPerWake;
            return this;
        }

        /// <summary>
        /// Max stack size per worker thread (default: 1 MiB). Useful when using large stackallocs
        /// in hot loops; keep conservative to avoid stack overflows under deep recursion or bursts.
        /// </summary>
        public UnhingedBuilder SetMaxStackSizePerThread(int maxStackSizePerThread)
        {
            _maxStackSizePerThread = maxStackSizePerThread;
            return this;
        }

        /// <summary>
        /// Provide a delegate that decides the worker count at build time.
        /// If not provided, defaults to <c>Environment.ProcessorCount / 2</c>.
        /// </summary>
        public UnhingedBuilder SetNWorkersSolver(Func<int>? solver)
        {
            _calculateNumberWorkers = solver;
            return this;
        }

        /// <summary>
        /// Inject the request handler used by workers to serve requests.
        /// </summary>
        public UnhingedBuilder InjectRequestHandler(Action<Connection> requestHandler)
        {
            _sRequestHandler = requestHandler;
            return this;
        }

        /// <summary>
        /// Finalize configuration:
        ///   - Logs arch and epoll_event packing
        ///   - Creates and configures the listening socket
        ///   - Resolves worker count via solver or default heuristic
        /// </summary>
        public UnhingedEngine Build()
        {
            Console.WriteLine(
                $"Arch={RuntimeInformation.ProcessArchitecture}, Packed={(Packed ? 12 : 16)}-byte epoll_event");

            CreateListenSocket();

            // Decide worker count (solver wins if supplied).
            _nWorkers = _calculateNumberWorkers is null
                ? Environment.ProcessorCount / 2
                : _calculateNumberWorkers();

            return _engine;
        }
    }
    
    /// <summary>
    /// Initializes the listening TCP socket:
    ///   - socket(AF_INET, SOCK_STREAM|CLOEXEC, IPPROTO_TCP)
    ///   - SO_REUSEADDR for quick restarts
    ///   - O_NONBLOCK for event-driven accept loop
    ///   - bind(0.0.0.0:port) and listen(backlog)
    /// Throws on failure with last P/Invoke errno.
    /// </summary>
    private static void CreateListenSocket()
    {
        // Create a TCP socket with close-on-exec.
        _listenFd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, IPPROTO_TCP);
        if (_listenFd < 0) throw new Exception($"socket failed errno={Marshal.GetLastPInvokeError()}");

        // Allow reusing the address to ease restarts.
        int one = 1;
        setsockopt(_listenFd, SOL_SOCKET, SO_REUSEADDR, ref one, sizeof(int));

        // Put the listening socket in non-blocking mode.
        int fl = fcntl(_listenFd, F_GETFL, 0);
        
        if (fl >= 0) 
            fcntl(_listenFd, F_SETFL, fl | O_NONBLOCK);

        // Bind 0.0.0.0:port
        var addr = new sockaddr_in
        {
            sin_family = (ushort)AF_INET,
            sin_port = Htons((ushort)_port),
            sin_addr = new in_addr { s_addr = 0 }, // 0.0.0.0 (INADDR_ANY)
            sin_zero = new byte[8]
        };
        if (bind(_listenFd, ref addr, (uint)Marshal.SizeOf<sockaddr_in>()) != 0)
            throw new Exception($"bind failed errno={Marshal.GetLastPInvokeError()}");
        
        // Start listening with the configured backlog.
        if (listen(_listenFd, _backlog) != 0)
            throw new Exception($"listen failed errno={Marshal.GetLastPInvokeError()}");
    }
}