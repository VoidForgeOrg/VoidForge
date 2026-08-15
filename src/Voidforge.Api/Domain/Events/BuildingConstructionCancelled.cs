namespace Voidforge.Api.Domain.Events;

// Player cancelled an in-progress construction (#72): the slot becomes a Cancelled tombstone
// (no refund). SlotIndex addresses the append-only Buildings list position, which stays stable.
public sealed record BuildingConstructionCancelled(int SlotIndex, DateTimeOffset At);
