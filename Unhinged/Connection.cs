namespace Unhinged;

internal unsafe class Connection : IDisposable
{
    internal int Head, Tail;     // [Head..Tail] valid
    internal readonly byte* RecBuf;
    internal FixedBufferWriter WriteBuffer;
    
    public Connection(int maxConnections, int inSlabSize, int outSlabSize)
    {
        RecBuf = (byte*)NativeMemory.AlignedAlloc((nuint)(maxConnections * inSlabSize),  64);
        WriteBuffer = new FixedBufferWriter(
            (byte*)NativeMemory.AlignedAlloc((nuint)(maxConnections * outSlabSize),  64), 
            outSlabSize);
    }

    public void Dispose()
    {
        // Dispose unmanaged buffers
        if (RecBuf != null)
            NativeMemory.AlignedFree(RecBuf);
        
        WriteBuffer.Dispose();
    }
}