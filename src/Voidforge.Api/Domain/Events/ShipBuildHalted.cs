namespace Voidforge.Api.Domain.Events;

// An active ship build paused because ingots ran dry (#83): the IronIngot buffer emptied and no
// ingots are being produced. Applied on the Planet stream; Apply sets the build Halted (dropping its
// ship-build drain out of RebaseRates and its full-power energy draw) and stamps HaltedAt for the
// resume recompute, while keeping the bay occupied for auto-start purposes. The ship-build sibling of
// the building-side ConstructionHalted.
public sealed record ShipBuildHalted(Guid BuildId, DateTimeOffset At);
