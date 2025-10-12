namespace Unhinged;

/// <summary>
/// Provides architecture-dependent helpers for low-level socket and epoll interop.
///
/// <para>
/// Linux’s <c>struct epoll_event</c> has different binary layouts depending on CPU architecture
/// (notably <b>12 bytes on x86/x64</b> and <b>16 bytes</b> on most other architectures like ARM/ARM64).
/// This class exposes constants and helpers to correctly read and write those structures
/// at runtime based on the process architecture.
/// </para>
///
/// <para>
/// These methods are used to serialize and deserialize <c>epoll_event</c> structures directly
/// into unmanaged buffers when interfacing with <c>epoll_wait</c>, <c>epoll_ctl</c>, and related syscalls.
/// </para>
/// </summary>
internal static unsafe class ProcessorArchDependant
{
    // =============================================================================================
    // Architecture-dependent configuration
    // =============================================================================================

    /// <summary>
    /// Indicates whether the current platform uses a <b>packed</b> epoll_event layout (12 bytes).
    /// <para>
    /// On x86 and x64 (little-endian), the epoll_event structure is packed to 12 bytes.
    /// On ARM, ARM64, and others, it uses natural 8-byte alignment, resulting in 16 bytes.
    /// </para>
    /// </summary>
    internal static readonly bool Packed =
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
        RuntimeInformation.ProcessArchitecture == Architecture.X86;

    /// <summary>
    /// The size (in bytes) of an <c>epoll_event</c> structure for the current runtime architecture.
    /// <para>
    /// Typically <c>12</c> bytes for packed x86/x64 layouts and <c>16</c> for natural alignment layouts.
    /// </para>
    /// </summary>
    internal static readonly int EvSize = Packed ? 12 : 16;

    // =============================================================================================
    // Struct read/write helpers
    // =============================================================================================

    /// <summary>
    /// Writes a Linux <c>epoll_event</c> structure into a preallocated unmanaged memory region.
    /// </summary>
    /// <param name="dest">Destination pointer to write the structure into.</param>
    /// <param name="events">Bitmask of epoll events (e.g. EPOLLIN, EPOLLOUT, EPOLLRDHUP, etc.).</param>
    /// <param name="fd">The file descriptor associated with the event.</param>
    /// <remarks>
    /// <para>
    /// Layouts by architecture:
    /// <list type="bullet">
    /// <item><description><b>Packed (x86/x64)</b>: <c>events @ 0 (4 bytes), data @ 4 (8 bytes)</c></description></item>
    /// <item><description><b>Natural (ARM/others)</b>: <c>events @ 0 (4 bytes), padding 4, data @ 8 (8 bytes)</c></description></item>
    /// </list>
    /// </para>
    /// Only the lower 32 bits of <paramref name="fd"/> are stored in the data field.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteEpollEvent(void* dest, uint events, int fd)
    {
        if (Packed)
        {
            // events @0 (4 bytes), data @4 (8 bytes)
            *(uint*)dest = events;
            *(ulong*)((byte*)dest + 4) = (uint)fd; // store fd in low 32 bits
        }
        else
        {
            // events @0 (4 bytes), pad 4, data @8 (8 bytes)
            *(uint*)dest = events;
            *(ulong*)((byte*)dest + 8) = (uint)fd;
        }
    }

    /// <summary>
    /// Reads a Linux <c>epoll_event</c> structure from unmanaged memory and extracts its fields.
    /// </summary>
    /// <param name="src">Pointer to the source buffer containing the epoll_event structure.</param>
    /// <param name="events">Outputs the event flags (EPOLLIN, EPOLLOUT, etc.).</param>
    /// <param name="fd">Outputs the associated file descriptor.</param>
    /// <remarks>
    /// Reads using the correct layout depending on the <see cref="Packed"/> flag.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReadEpollEvent(void* src, out uint events, out int fd)
    {
        if (Packed)
        {
            events = *(uint*)src;
            fd = (int)*(uint*)((byte*)src + 4);
        }
        else
        {
            events = *(uint*)src;
            fd = (int)*(uint*)((byte*)src + 8);
        }
    }

    // =============================================================================================
    // Networking helpers
    // =============================================================================================

    /// <summary>
    /// Converts a 16-bit unsigned integer from host byte order to network byte order (big-endian).
    /// </summary>
    /// <param name="x">The value to convert.</param>
    /// <returns>The converted value in network byte order.</returns>
    /// <remarks>
    /// Equivalent to the native <c>htons()</c> function from the BSD sockets API.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Htons(ushort x) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(x) : x;
}