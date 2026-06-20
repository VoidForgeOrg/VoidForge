namespace Voidforge.Api.Domain.Events;

// The building's effect (e.g. a Drill's extraction rate) is derived from BuildingSpecs when
// applied — kept out of the event so balance values live in one place in the domain.
public sealed record BuildingPlaced(BuildingType BuildingType, DateTimeOffset PlacedAt);
