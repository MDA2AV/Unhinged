using System.Buffers;

namespace Unhinged;

public sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
{
    private readonly byte* _ptr;
    private readonly int _length;
    public UnmanagedMemoryManager(byte* ptr, int length) { _ptr = ptr; _length = length; }
    
    public override Span<byte> GetSpan() => new(_ptr, _length);
    public override MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_ptr + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}