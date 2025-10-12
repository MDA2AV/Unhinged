namespace Unhinged;

// Very naive
internal static unsafe class HeaderParsing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte[] buf, int head, int tail)
    {
        int idx = buf.AsSpan(head, tail - head).IndexOf("\r\n\r\n"u8);
        return idx >= 0 ? head + idx : -1;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte* buf, int head, int tail)
    {
        // Construct a Span<byte> view over the raw memory.
        // The caller must guarantee that (tail - head) bytes are valid and readable.
        var span = new ReadOnlySpan<byte>(buf + head, tail - head);

        int idx = span.IndexOf("\r\n\r\n"u8);
        return idx >= 0 ? head + idx : -1;
    }
}