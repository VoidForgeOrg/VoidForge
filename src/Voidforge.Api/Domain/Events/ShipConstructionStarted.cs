namespace Voidforge.Api.Domain.Events;

public sealed record ShipConstructionStarted(Guid BuildId, DateTimeOffset StartedAt, DateTimeOffset CompletesAt);
