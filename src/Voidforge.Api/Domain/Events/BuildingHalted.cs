namespace Voidforge.Api.Domain.Events;

// A producer halted because its output storage pool is full (#69). Applied on the Planet stream;
// Apply drops the slot out of the Operational set and re-derives rates.
public sealed record BuildingHalted(int SlotIndex, HaltReason Reason, DateTimeOffset At);
