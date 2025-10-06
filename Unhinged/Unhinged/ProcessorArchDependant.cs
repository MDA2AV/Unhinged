namespace Unhinged;

internal static unsafe class ProcessorArchDependant
{
    // ===== Runtime arch-dependent epoll_event size (x86-64 packed=12, most others natural=16) =====
    internal static readonly bool Packed = RuntimeInformation.ProcessArchitecture == Architecture.X64 
                                           || RuntimeInformation.ProcessArchitecture == Architecture.X86;
    internal static readonly int EvSize = Packed ? 12 : 16;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Htons(ushort x) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(x) : x;
}