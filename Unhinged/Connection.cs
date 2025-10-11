namespace Unhinged;

internal ref struct Connection
{
    //internal int Fd;
    //internal int Index;
    // Reading Buffer
    //internal byte* RecBuf;
    //internal int RecHead, RecTail;
    
    internal byte[] Buf = new byte[4096 * 16];
    internal int Head, Tail;     // [Head..Tail) valid

    public Connection()
    {
        
    }
    
    internal FixedBufferWriter WriteBuffer;
    
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