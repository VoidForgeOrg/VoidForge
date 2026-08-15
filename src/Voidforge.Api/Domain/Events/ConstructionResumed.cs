namespace Voidforge.Api.Domain.Events;

// A paused in-flight building construction resumed once ingots returned (#83). Applied on the Planet
// stream; Apply restores the slot to UnderConstruction with CompletesAt pushed out by the paused
// duration (resumeAt + (CompletesAt − HaltedAt)) and clears HaltedAt. The ingot-consumer mirror of
// BuildingResumed.
public sealed record ConstructionResumed(int SlotIndex, DateTimeOffset At);
