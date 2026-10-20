namespace Alcoler.Adapters.Mcp;

internal class McpProtocolException : Exception
{
    internal McpProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class McpTransportException : McpProtocolException
{
    internal McpTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class McpRemoteException : McpProtocolException
{
    internal McpRemoteException(string message, int code)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
