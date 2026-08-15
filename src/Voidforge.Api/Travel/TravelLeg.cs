namespace Voidforge.Api.Travel;

public sealed record TravelLeg(
    Guid? WaypointPlanetId,     // null in MVP — the single leg ends at the destination
    decimal Distance,
    DateTimeOffset ArrivesAt);
