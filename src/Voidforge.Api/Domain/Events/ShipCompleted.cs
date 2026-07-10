namespace Voidforge.Api.Domain.Events;

public sealed record ShipCompleted(Guid BuildId, DateTimeOffset CompletedAt);
