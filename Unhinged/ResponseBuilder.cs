namespace Unhinged;

// ReSharper disable always SuggestVarOrType_BuiltInTypes
// ReSharper disable always SuggestVarOrType_Elsewhere

// Still very basic static responses
// TODO: Completely refactor this
internal static class ResponseBuilder
{
    internal static byte[] Build200()
    {
        ReadOnlySpan<byte> body = "{\"message\":\"Hello, World!\"}"u8;
        string head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json; charset=UTF-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n";
        byte[] hb = System.Text.Encoding.ASCII.GetBytes(head);
        byte[] buf = new byte[hb.Length + body.Length];
        Buffer.BlockCopy(hb, 0, buf, 0, hb.Length);
        body.CopyTo(buf.AsSpan(hb.Length));
        return buf;
    }

    internal static byte[] BuildSimpleResponse(int status, string reason)
    {
        ReadOnlySpan<byte> body = "{}"u8;
        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] hb = System.Text.Encoding.ASCII.GetBytes(head);
        byte[] buf = new byte[hb.Length + body.Length];
        Buffer.BlockCopy(hb, 0, buf, 0, hb.Length);
        body.CopyTo(buf.AsSpan(hb.Length));
        return buf;
    }
}