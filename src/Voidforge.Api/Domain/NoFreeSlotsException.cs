namespace Voidforge.Api.Domain;

// Raised when a building is placed on a planet whose building slots are all occupied.
public sealed class NoFreeSlotsException : Exception
{
    public NoFreeSlotsException()
    {
    }

    public NoFreeSlotsException(string message)
        : base(message)
    {
    }

    public NoFreeSlotsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
