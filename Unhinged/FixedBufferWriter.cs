using System.Buffers;

namespace Unhinged;

internal unsafe struct FixedBufferWriter : IBufferWriter<byte>
{
    private readonly int _capacity;

    internal int Head;

    public FixedBufferWriter(byte* ptr, int capacity)
    {
        Ptr = ptr;
        _capacity = capacity;
        Tail = 0;

        Head = 0;
    }
    
    internal void Reset() {
        Tail = 0;
        Head = 0;
    }

    internal int Tail { get; private set; }
    internal byte* Ptr { get; }

    public void Advance(int count) => Tail += count;

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Tail + sizeHint > _capacity)
            throw new InvalidOperationException("Buffer too small.");
        
        return new Span<byte>(Ptr + Tail, _capacity - Tail);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) =>
        throw new NotSupportedException();
    
    internal void Write(ReadOnlySpan<byte> source)
    {
        if (Tail + source.Length > _capacity)
            throw new InvalidOperationException("Buffer too small.");
        
        source.CopyTo(new Span<byte>(Ptr + Tail, _capacity - Tail));
        Tail += source.Length;
    }
}

