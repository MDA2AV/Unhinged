namespace Unhinged;

internal static unsafe class Native
{
    // ===== P/Invoke =====
    [DllImport("libc", SetLastError = true)] internal static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] internal static extern int bind(int sockfd, ref sockaddr_in addr, uint addrlen);
    [DllImport("libc", SetLastError = true)] internal static extern int listen(int sockfd, int backlog);
    [DllImport("libc", SetLastError = true)] internal static extern int accept4(int sockfd, IntPtr addr, IntPtr addrlen, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int setsockopt(int sockfd, int level, int optname, ref int optval, uint optlen);
    [DllImport("libc", SetLastError = true)] internal static extern int setsockopt(int sockfd, int level, int optname, ref Linger optval, uint optlen);
    [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] internal static extern long read(int fd, IntPtr buf, ulong count);
    [DllImport("libc", SetLastError = true)] internal static extern long write(int fd, IntPtr buf, ulong count);
    [DllImport("libc", SetLastError = true)] internal static extern long recv(int sockfd, IntPtr buf, ulong len, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern long send(int sockfd, IntPtr buf, ulong len, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern long send(int sockfd, byte* buf, long len, int flags);
    [DllImport("libc", SetLastError = true)] public static extern nint send(int sockfd, void* buf, nuint len, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int epoll_create1(int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int epoll_ctl(int epfd, int op, int fd, IntPtr ev);
    [DllImport("libc", SetLastError = true)] internal static extern int epoll_wait(int epfd, IntPtr events, int maxevents, int timeout);
    [DllImport("libc", SetLastError = true)] internal static extern int eventfd(uint initval, int flags);
    
    // ===== structs & constants =====
    [StructLayout(LayoutKind.Sequential)]
    internal struct in_addr { public uint s_addr; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct sockaddr_in
    {
        public ushort sin_family;
        public ushort sin_port;
        public in_addr sin_addr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Linger { public int l_onoff; public int l_linger; }

    internal const int AF_INET = 2;
    internal const int SOCK_STREAM = 1;
    internal const int IPPROTO_TCP = 6;
    internal const int SOL_SOCKET = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_LINGER = 13;
    internal const int TCP_NODELAY = 1;

    internal const int O_NONBLOCK = 0x800;
    internal const int F_GETFL = 3;
    internal const int F_SETFL = 4;

    internal const int EPOLLIN = 0x001;
    internal const int EPOLLOUT = 0x004;
    internal const int EPOLLERR = 0x008;
    internal const int EPOLLHUP = 0x010;
    internal const int EPOLLRDHUP = 0x2000;

    internal const int EPOLL_CTL_ADD = 1;
    internal const int EPOLL_CTL_DEL = 2;
    internal const int EPOLL_CTL_MOD = 3;

    internal const int EPOLL_CLOEXEC = 0x80000;
    internal const int SOCK_CLOEXEC = 0x80000;
    internal const int SOCK_NONBLOCK = 0x800;

    internal const int EFD_NONBLOCK = 0x800;
    internal const int EFD_CLOEXEC = 0x80000;

    internal const int MSG_NOSIGNAL = 0x4000;

    internal const int EINTR = 4;
    internal const int EAGAIN = 11;
    internal const int EWOULDBLOCK = 11;
    internal const int EPIPE = 32;
    internal const int ECONNABORTED = 103;
    internal const int ECONNRESET = 104;
}