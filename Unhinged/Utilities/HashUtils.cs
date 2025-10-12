// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

/// <summary>
/// Provides extremely lightweight hashing utilities optimized for short, hot-path inputs
/// (e.g. HTTP routes, header names, or method tokens).
/// </summary>
internal static class HashUtils
{
    /// <summary>
    /// Computes a 32-bit FNV-1a (Fowler–Noll–Vo) hash of the given byte span.
    /// </summary>
    /// <remarks>
    /// FNV-1a is a simple, fast, non-cryptographic hash function designed for small inputs
    /// and stable distribution.  
    /// 
    /// Formula:
    /// <code>
    /// h = 2166136261
    /// for each byte b in data:
    ///     h = (h XOR b) * 16777619
    /// </code>
    ///
    /// Characteristics:
    ///  • 32-bit unsigned integer output  
    ///  • Deterministic and endian-independent  
    ///  • Good avalanche behavior for short ASCII inputs  
    ///  • Not suitable for security-critical use (collision-prone vs. modern hashes)
    /// </remarks>
    /// <param name="data">Input data to hash (typically a small UTF-8 slice).</param>
    /// <returns>32-bit unsigned FNV-1a hash of <paramref name="data"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        uint h = offset;

        for (int i = 0; i < data.Length; i++)
        {
            h ^= data[i];
            h *= prime;
        }

        return h;
    }
}