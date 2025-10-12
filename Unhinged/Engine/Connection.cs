namespace Unhinged;

/// <summary>
/// Per-connection buffers backed by unmanaged memory:
/// - <see cref="ReceiveBuffer"/>: receive buffer (unmanaged slab)
/// - <see cref="WriteBuffer"/>: send buffer writer (unmanaged slab)
/// </summary>
[SkipLocalsInit]
internal unsafe class Connection : IDisposable
{
    /// <summary>Read window: bytes are valid in [<see cref="Head"/> ... <see cref="Tail"/>)</summary>
    internal int Head, Tail;

    /// <summary>Base pointer for the receiving slab.</summary>
    internal readonly byte* ReceiveBuffer;

    /// <summary>Writer over the send slab.</summary>
    internal readonly FixedBufferWriter WriteBuffer;

    /// <param name="maxConnections">Used to size the slabs (typically per-worker slab size).</param>
    /// <param name="inSlabSize">Bytes per connection for receive.</param>
    /// <param name="outSlabSize">Bytes per connection for send.</param>
    public Connection(int maxConnections, int inSlabSize, int outSlabSize)
    {
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