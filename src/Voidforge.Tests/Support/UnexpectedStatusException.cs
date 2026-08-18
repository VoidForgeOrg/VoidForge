namespace Voidforge.Tests.Support;

/// <summary>
/// Thrown when a response's status is not the expected code for a modeled non-200 path
/// (for example 403/409/503). Callers that tolerate contention MAY catch this; a 5xx is
/// never surfaced this way — it throws <see cref="ServerErrorException"/> instead.
/// </summary>
public sealed class UnexpectedStatusException : Exception
{
    public UnexpectedStatusException()
    {
    }

    public UnexpectedStatusException(string message)
        : base(message)
    {
    }

    public UnexpectedStatusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnexpectedStatusException(int expected, int actual, string method, string path)
        : base($"Unexpected status: {method} {path} -> expected {expected}, got {actual}")
    {
        Expected = expected;
        Actual = actual;
        Method = method;
        Path = path;
    }

    public int Expected { get; }

    public int Actual { get; }

    public string Method { get; } = string.Empty;

    public string Path { get; } = string.Empty;
}
