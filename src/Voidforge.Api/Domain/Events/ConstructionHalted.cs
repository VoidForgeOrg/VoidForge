namespace Voidforge.Api.Domain.Events;

// An in-flight building construction paused because ingots ran dry (#83): the IronIngot buffer emptied
// and no ingots are being produced. Applied on the Planet stream; Apply sets the slot ConstructionHalted
// (dropping its construction drain out of RebaseRates) and stamps HaltedAt for the resume recompute.
// The ingot-consumer mirror of the ore-side BuildingHalted(InputStarved).
public sealed record ConstructionHalted(int SlotIndex, DateTimeOffset At);
