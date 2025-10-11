namespace Unhinged;

// Alternative Utf8JsonWriter for stack allocated buffers
// While this looks like "cheating", it isn't because serialization is doing the same crap under the hood
// "scanning" objects and creating source generated code to manually create your json.
internal ref struct TrivialUtf8JsonWriter_JsonMessage
{
    private IUnmanagedBufferWriter<byte> _buffer;

    internal TrivialUtf8JsonWriter_JsonMessage(IUnmanagedBufferWriter<byte> buffer) => _buffer = buffer;
    
    internal void SetWriter(IUnmanagedBufferWriter<byte> buffer) => _buffer = buffer;
    internal IUnmanagedBufferWriter<byte> GetWriter() => _buffer;

    internal void Serialize_JsonMessage()
    {
        
    }
    
}

internal ref struct SimpleJsonWriter
{
    private ISpanWriter<byte> _buffer;
    private bool _firstProperty;
    private Stack<bool> _firstPropertyStack; // Track nesting

    public SimpleJsonWriter(ISpanWriter<byte> buffer)
    {
        _buffer = buffer;
        _firstProperty = true;
        _firstPropertyStack = new Stack<bool>();
    }

    private void WriteRaw(scoped ReadOnlySpan<byte> bytes)
    {
        Span<byte> span = _buffer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _buffer.Advance(bytes.Length);
    }

    private void WriteRaw(byte b)
    {
        Span<byte> span = _buffer.GetSpan(1);
        span[0] = b;
        _buffer.Advance(1);
    }

    public void WriteStartObject()
    {
        _firstPropertyStack.Push(_firstProperty);
        _firstProperty = true;
        WriteRaw((byte)'{');
    }

    public void WriteEndObject()
    {
        _firstProperty = _firstPropertyStack.Pop();
        WriteRaw((byte)'}');
    }

    public void WriteStartArray()
    {
        _firstPropertyStack.Push(_firstProperty);
        _firstProperty = true;
        WriteRaw((byte)'[');
    }

    public void WriteEndArray()
    {
        _firstProperty = _firstPropertyStack.Pop();
        WriteRaw((byte)']');
    }

    public void WritePropertyName(scoped ReadOnlySpan<byte> utf8Name)
    {
        if (!_firstProperty)
            WriteRaw((byte)',');
        
        WriteRaw((byte)'"');
        WriteRaw(utf8Name);
        WriteRaw((byte)'"');
        WriteRaw((byte)':');
        
        _firstProperty = false;
    }

    public void WritePropertyName(string name)
    {
        Span<byte> utf8 = stackalloc byte[name.Length * 3]; // Max UTF8 expansion
        int written = System.Text.Encoding.UTF8.GetBytes(name, utf8);
        WritePropertyName(utf8.Slice(0, written));
    }

    public void WriteStringValue(scoped ReadOnlySpan<byte> utf8Value)
    {
        WriteRaw((byte)'"');
        
        // Need to escape special characters
        foreach (byte b in utf8Value)
        {
            if (b == '"' || b == '\\')
            {
                WriteRaw((byte)'\\');
                WriteRaw(b);
            }
            else if (b < 32)
            {
                // Control characters - escape as \uXXXX
                WriteRaw("\\u00"u8);
                WriteHexByte(b);
            }
            else
            {
                WriteRaw(b);
            }
        }
        
        WriteRaw((byte)'"');
    }

    public void WriteStringValue(string value)
    {
        Span<byte> utf8 = stackalloc byte[value.Length * 3];
        int written = System.Text.Encoding.UTF8.GetBytes(value, utf8);
        WriteStringValue(utf8.Slice(0, written));
    }

    public void WriteNumberValue(int value)
    {
        Span<byte> buffer = stackalloc byte[11]; // Max int32 is 11 chars including sign
        if (value.TryFormat(buffer, out int written, default, System.Globalization.CultureInfo.InvariantCulture))
        {
            WriteRaw(buffer.Slice(0, written));
        }
    }

    public void WriteNumberValue(long value)
    {
        Span<byte> buffer = stackalloc byte[20];
        if (value.TryFormat(buffer, out int written, default, System.Globalization.CultureInfo.InvariantCulture))
        {
            WriteRaw(buffer.Slice(0, written));
        }
    }

    public void WriteNumberValue(double value)
    {
        Span<byte> buffer = stackalloc byte[32];
        if (value.TryFormat(buffer, out int written, default, System.Globalization.CultureInfo.InvariantCulture))
        {
            WriteRaw(buffer.Slice(0, written));
        }
    }

    public void WriteBooleanValue(bool value)
    {
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteNullValue()
    {
        WriteRaw("null"u8);
    }

    private void WriteHexByte(byte b)
    {
        const string hex = "0123456789abcdef";
        WriteRaw((byte)hex[b >> 4]);
        WriteRaw((byte)hex[b & 0x0F]);
    }

    public void Flush()
    {
        // Nothing to flush - we write directly
    }
}