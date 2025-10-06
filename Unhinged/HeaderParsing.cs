namespace Unhinged;

// Very naive
internal static class HeaderParsing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte[] buf, int head, int tail)
    {
        int idx = buf.AsSpan(head, tail - head).IndexOf("\r\n\r\n"u8);
        return idx >= 0 ? head + idx : -1;
    }
}