// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

// Very naive
internal static unsafe class HeaderParsing
{
    /// <summary>
    /// Finds the first occurrence of CRLFCRLF in a managed byte buffer slice [head..tail).
    /// Returns the absolute index (relative to buf) of the '\r' in the sequence, or -1 if not found.
    /// </summary>
    /// <remarks>
    /// Fast path: relies on <see cref="Span{T}.IndexOf(ReadOnlySpan{T})"/> which is vectorized on modern runtimes.
    /// The caller must ensure 0 ≤ head ≤ tail ≤ buf.Length.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte[] buf, int head, int tail)
    {
        int idx = buf.AsSpan(head, tail - head).IndexOf(CrlfCrlf);
        return idx >= 0 ? head + idx : -1;
    }
    
    /// <summary>
    /// Finds CRLFCRLF within an unmanaged region addressed by <paramref name="buf"/> in [head..tail).
    /// Returns the absolute index (relative to the same coordinate system as head/tail) of the '\r' or -1.
    /// </summary>
    /// <remarks>
    /// The caller must guarantee that the memory range [buf+head, buf+tail) is valid and readable.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindCrlfCrlf(byte* buf, int head, int tail)
    {
        // Construct a Span<byte> view over the raw memory.
        // The caller must guarantee that (tail - head) bytes are valid and readable.
        var span = new ReadOnlySpan<byte>(buf + head, tail - head);

        int idx = span.IndexOf(CrlfCrlf);
        return idx >= 0 ? head + idx : -1;
    }
    
    /// <summary>
    /// Same as the unmanaged overload, but also returns a <see cref="ReadOnlySpan{Byte}"/> view over [head..tail).
    /// Useful to avoid reconstructing the span twice (for parsing the request-line after the sentinel is found).
    /// </summary>
    /// <param name="buf">Base pointer to the buffer.</param>
    /// <param name="head">Start offset (inclusive).</param>
    /// <param name="tail">End offset (exclusive).</param>
    /// <param name="idx">
    /// Out: absolute index of the '\r' starting the CRLFCRLF sentinel, or -1 if not found.
    /// </param>
    /// <returns>A span over the provided range [head..tail).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> FindCrlfCrlf(byte* buf, int head, int tail, out int idx)
    {
        // Construct a Span<byte> view over the raw memory.
        // The caller must guarantee that (tail - head) bytes are valid and readable.
        var span = new ReadOnlySpan<byte>(buf + head, tail - head);

        idx = span.IndexOf(CrlfCrlf);
        if (idx >= 0)
            idx += head;
        else
            idx = -1;

        return span;
    }

    /// <summary>
    /// Extracts and hashes the HTTP request target from the request line (e.g., "GET /path?x=1 HTTP/1.1").
    /// Expects <paramref name="headerSpan"/> to begin at the start of the request line and contain at least
    /// the first CRLF. Throws <see cref="InvalidOperationException"/> if the request-line is malformed.
    /// </summary>
    /// <remarks>
    /// Parsing steps:
    ///   1) Find the first CRLF to isolate the request line.
    ///   2) Find first and second spaces: METHOD SP REQUEST-TARGET SP HTTP-VERSION.
    ///   3) Slice the REQUEST-TARGET and hash it with FNV-1a 32-bit.
    /// 
    /// Notes:
    ///   - The hash is over the raw byte sequence of the target (no decoding / normalization).
    ///   - Query string is preserved in the slice; callers can change hashing if they want to ignore it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ExtractRoute(ReadOnlySpan<byte> headerSpan)
    {
        var lineEnd = headerSpan.IndexOf(Crlf);
        var firstHeader = headerSpan[..lineEnd];

        var firstSpace = firstHeader.IndexOf(Space);
        if (firstSpace == -1)
            throw new InvalidOperationException("Invalid request line");
        
        var secondSpaceRelative = firstHeader[(firstSpace + 1)..].IndexOf(Space);
        if (secondSpaceRelative == -1)
            throw new InvalidOperationException("Invalid request line");

        var secondSpace = firstSpace + secondSpaceRelative + 1;
        
        // REQUEST-TARGET slice: may include path + query (e.g., "/foo?bar=baz")
        var url = firstHeader[(firstSpace + 1)..secondSpace];

        return Fnv1a32(url);
    }
    
    // ===== Common tokens (kept as ReadOnlySpan<byte> for zero-allocation literals) =====

    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;
    private static ReadOnlySpan<byte> CrlfCrlf => "\r\n\r\n"u8;

    // ASCII byte codes (documented for clarity)
    private const byte Space = 0x20;        // ' '
    private const byte Question = 0x3F;     // '?'
    private const byte QuerySeparator = 0x26; // '&'
    private const byte Equal = 0x3D;        // '='
    private const byte Colon = 0x3A;        // ':'
    private const byte SemiColon = 0x3B;    // ';'
}