namespace Voidforge.Api.Domain.Events;

// A paused ship build resumed once ingots returned (#83). Applied on the Planet stream; Apply restores
// the build to Active with CompletesAt pushed out by the paused duration (resumeAt + (CompletesAt −
// HaltedAt)) and clears HaltedAt. The ship-build sibling of the building-side ConstructionResumed.
public sealed record ShipBuildResumed(Guid BuildId, DateTimeOffset At);
