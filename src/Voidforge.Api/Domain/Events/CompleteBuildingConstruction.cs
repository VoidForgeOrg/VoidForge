namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at CompletesAt. The handler
// checkpoints the aggregate at this scheduled time, not delivery time, so values stay exact.
public sealed record CompleteBuildingConstruction(Guid PlanetId, int SlotIndex, DateTimeOffset CompletesAt);
