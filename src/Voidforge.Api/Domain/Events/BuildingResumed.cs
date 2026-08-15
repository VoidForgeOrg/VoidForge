namespace Voidforge.Api.Domain.Events;

// A halted producer resumed because its output storage pool freed up (#69). Apply restores the
// slot to Operational and re-derives rates.
public sealed record BuildingResumed(int SlotIndex, DateTimeOffset At);
