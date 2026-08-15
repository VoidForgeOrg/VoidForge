namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at CompletesAt to finish a two-step
// demolition. The handler checkpoints the aggregate at this scheduled time, not delivery time, so
// values stay exact. Mirrors CompleteBuildingConstruction; validate-on-arrival makes redelivery safe.
public sealed record CompleteBuildingDemolition(Guid PlanetId, int SlotIndex, DateTimeOffset CompletesAt);
