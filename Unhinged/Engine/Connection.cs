// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated
#pragma warning disable CA2014

namespace Unhinged;

/// <summary>
/// Per-connection buffers backed by unmanaged memory:
/// - <see cref="ReceiveBuffer"/>: receive buffer (unmanaged slab)
/// - <see cref="WriteBuffer"/>: send buffer writer (unmanaged slab)
/// </summary>
[SkipLocalsInit]
public unsafe class Connection : IDisposable
{
    /// <summary>Read window: bytes are valid in [<see cref="Head"/> ... <see cref="Tail"/>)</summary>
    public int Head, Tail;

    /// <summary>Base pointer for the receiving slab.</summary>
    public readonly byte* ReceiveBuffer;

    /// <summary>Writer over the send slab.</summary>
    public readonly FixedBufferWriter WriteBuffer;
    
    // <summary>Fnv1a32 hashed route</summary>
    internal uint HashedRoute { get; set; }

    /// <param name="maxConnections">Used to size the slabs (typically per-worker slab size).</param>
    /// <param name="inSlabSize">Bytes per connection for receive.</param>
    /// <param name="outSlabSize">Bytes per connection for send.</param>
    public Connection(int maxConnections, int inSlabSize, int outSlabSize)
    {
        //AlignedAlloc(size, 64) ensures your memory starts at an address that’s a multiple of 64,
        //matching CPU cache-line size, reducing false sharing and improving SIMD/cache performance.
        ReceiveBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(maxConnections * inSlabSize), 64);
        WriteBuffer = new FixedBufferWriter(
            (byte*)NativeMemory.AlignedAlloc((nuint)(maxConnections * outSlabSize), 64),
            outSlabSize);
    }

    /// <summary>
    /// Frees the unmanaged slabs. Call exactly once when the connection is permanently done.
    /// </summary>
    public void Dispose()
    {
        if (ReceiveBuffer != null)
            NativeMemory.AlignedFree(ReceiveBuffer);

        WriteBuffer.Dispose();
        GC.SuppressFinalize(this);
    }
}