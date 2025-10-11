namespace Unhinged;

[SkipLocalsInit]
internal unsafe ref struct FixedBufferWriter : IUnmanagedBufferWriter<byte>
{
    private readonly int _capacity;
    
    internal int Head;
    internal int Tail { get; private set; }
    internal byte* Ptr { get; }

    public FixedBufferWriter(byte* ptr, int capacity) { Ptr = ptr; _capacity = capacity; Reset(); }
    
    internal void Reset() { Tail = 0; Head = 0; }
    public void Advance(int count) => Tail += count;
    
    public byte* GetPointer() => Ptr;

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Tail + sizeHint > _capacity)
            throw new InvalidOperationException("Buffer too small.");
        
        return new Span<byte>(Ptr + Tail, _capacity - Tail);
    }

    public void WriteUnmanaged(ReadOnlySpan<byte> source)
    {
        if (Tail + source.Length > _capacity)
            throw new InvalidOperationException("Buffer too small.");
        
        fixed (byte* src = source)
            Buffer.MemoryCopy(src, Ptr + Tail, _capacity - Tail, source.Length);
        
        Tail += source.Length;
    }
    
    public void Write(ReadOnlySpan<byte> source)
    {
        if (Tail + source.Length > _capacity)
            throw new InvalidOperationException("Buffer too small.");
        
        source.CopyTo(new Span<byte>(Ptr + Tail, _capacity - Tail));
        Tail += source.Length;
    }
}