using Voidforge.Api.Domain;

namespace Voidforge.Api.Domain.Events;

public sealed record ShipConstructionQueued(
    Guid BuildId,
    ShipType Type,
    DateTimeOffset QueuedAt,
    decimal DrainPerSecond,
    decimal BuildDurationSeconds);
