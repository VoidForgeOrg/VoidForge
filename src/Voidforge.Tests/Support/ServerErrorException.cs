namespace Voidforge.Tests.Support;

/// <summary>
/// Thrown when a harness request receives a 5xx server error (except the modeled 503).
/// This is the universal "no 500" tripwire: nothing in the test harness or the soak driver
/// should ever catch it, so a genuine server error always fails the test.
/// </summary>
public sealed class ServerErrorException : Exception
{
    public ServerErrorException()
    {
    }

    public ServerErrorException(string message)
        : base(message)
    {
    }

    public ServerErrorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ServerErrorException(int statusCode, string method, string path, string? body)
        : base($"Server error: {method} {path} -> {statusCode}\n{body}")
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        Body = body;
    }

    public int StatusCode { get; }

    public string Method { get; } = string.Empty;

    public string Path { get; } = string.Empty;

    public string? Body { get; }
}
