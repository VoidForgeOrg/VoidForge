namespace Voidforge.Api.Domain.Events;

// Player started demolishing a completed building (#72). This is the IMMEDIATE-shutdown step of the
// two-step teardown: the slot flips to Demolishing (leaves the Operational set → zero generation,
// draw and production, so the D9 "energy freed → overload resolves" cascade re-derives here), while
// still occupying its slot until CompletesAt, when BuildingDemolished frees it. SlotIndex addresses
// the append-only Buildings list position, which stays stable.
public sealed record BuildingDemolitionStarted(int SlotIndex, DateTimeOffset At, DateTimeOffset CompletesAt);
