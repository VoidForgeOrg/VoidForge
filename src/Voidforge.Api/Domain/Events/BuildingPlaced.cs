namespace Voidforge.Api.Domain.Events;

// IronOreExtractionRate is only meaningful for Drills (0 for other building types in Phase 2).
public sealed record BuildingPlaced(
    BuildingType BuildingType,
    decimal IronOreExtractionRate,
    DateTimeOffset PlacedAt);
