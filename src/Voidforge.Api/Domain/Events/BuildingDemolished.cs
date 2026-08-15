namespace Voidforge.Api.Domain.Events;

// Demolition teardown completed (#72): the slot becomes a Demolished tombstone (terminal). It keeps
// its list position so SlotIndex stays a stable monotonic identifier, but LiveBuildingCount frees
// the slot. Delivered by the durable CompleteBuildingDemolition message at the scheduled CompletesAt.
public sealed record BuildingDemolished(int SlotIndex, DateTimeOffset At);
