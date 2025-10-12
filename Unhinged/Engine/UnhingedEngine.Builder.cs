// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

public sealed partial class UnhingedEngine
{
    public static UnhingedBuilder CreateBuilder() => new UnhingedBuilder();

    private static int _port = 8080;
    private static int _backlog = 16384;
    private static int _maxNumberConnectionsPerWorker = 1024;
    private static int _inSlabSize = 16 * 1024;
    private static int _outSlabSize = 16 * 1024;
    private static int _maxEventsPerWake  = 16;
    private static int _maxStackSizePerThread  = 1024 * 1024;
    private static int _listenFd;
    private static int _acceptBlocking;
    private static int _nWorkers;
    
    private static Func<int>? _calculateNumberWorkers;

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
    
    public sealed class UnhingedBuilder
    {
        private readonly UnhingedEngine _engine;
        public UnhingedBuilder() => _engine = new UnhingedEngine();

        public UnhingedBuilder SetPort(int port)
        {
            _port = port;
            return this;
        }

        public UnhingedBuilder SetBacklog(int backlog)
        {
            _backlog = backlog;
            return this;
        }

        public UnhingedBuilder SetMaxNumberConnectionsPerWorker(int maxNumberConnectionsPerWorker)
        {
            _maxNumberConnectionsPerWorker = maxNumberConnectionsPerWorker;
            return this;
        }

        public UnhingedBuilder SetSlabSizes(int inSlabSize, int outSlabSize)
        {
            _inSlabSize = inSlabSize;
            _outSlabSize = outSlabSize;
            return this;
        }

        public UnhingedBuilder SetMaxEventsPerWake(int maxEventsPerWake)
        {
            _maxEventsPerWake = maxEventsPerWake;
            return this;
        }

        public UnhingedBuilder SetMaxStackSizePerThread(int maxStackSizePerThread)
        {
            _maxStackSizePerThread = maxStackSizePerThread;
            return this;
        }

        public UnhingedBuilder SetNWorkersSolver(Func<int>? solver)
        {
            _calculateNumberWorkers = solver;
            return this;
        }

        public UnhingedBuilder InjectRequestHandler(Action<Connection> requestHandler)
        {
            _sRequestHandler = requestHandler;
            return this;
        }

        public UnhingedEngine Build()
        {
            Console.WriteLine(
                $"Arch={RuntimeInformation.ProcessArchitecture}, Packed={(Packed ? 12 : 16)}-byte epoll_event");

            CreateListenSocket();

            if (_calculateNumberWorkers is null)
                _nWorkers = Environment.ProcessorCount / 2;
            else
                _nWorkers = _calculateNumberWorkers();

            return _engine;
        }
    }
    
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
        
        if (listen(_listenFd, _backlog) != 0)
            throw new Exception($"listen failed errno={Marshal.GetLastPInvokeError()}");
    }
}