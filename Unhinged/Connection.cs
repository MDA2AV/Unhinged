namespace Unhinged;


// TODO: This class needs a rework, CompactIfNeeded() is too expensive, consider using a similar approach to Sequence<byte>
// TODO: with multiple segment approach if the buffer is not large enough?
// TODO: For non pipeline clients, shouldn't be a big problem, most headers are smaller than 4096 bytes.
internal sealed unsafe class Connection
{
    internal int Fd;
    
    // Reading Buffer
    internal byte[] Buf = new byte[4096];
    internal int Head, Tail;     // [Head..Tail) valid
    
    // Writing Buffer
    // When response data is calculated, should be written into this buffer
    internal byte* WrBuf;
    
    internal bool WantWrite;
    internal int RespSent;

    // How many bytes were already sent 
    internal int wrHead;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompactIfNeeded()
    {
        if (Head > 0 && Head < Tail)
        {
            Buffer.BlockCopy(Buf, Head, Buf, 0, Tail - Head);
            Tail -= Head; Head = 0;
        }
        else if (Head >= Tail)
        {
            Head = Tail = 0; 
        }
    }
}