namespace Voidforge.Api.Domain.Events;

// Carries the derived drain and completion time (computed at the endpoint from BalanceOptions)
// so the aggregate Apply stays pure — no config lookup during replay.
public sealed record BuildingConstructionStarted(
    int SlotIndex,
    BuildingType BuildingType,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletesAt,
    decimal DrainPerSecond);
