namespace Voidforge.Api.Documents;

public sealed class ApiKey
{
    public Guid Id { get; set; }
    public required string HashedKey { get; set; }
    public required Guid PlayerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
