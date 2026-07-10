namespace Voidforge.Api.Domain;

// CompletesAt and ConstructionDrainPerSecond are populated only while UnderConstruction
// (both null/0 for Operational slots, including homeworld-seeded buildings).
public sealed record BuildingSlot(
    BuildingType Type,
    BuildingStatus Status,
    DateTimeOffset? CompletesAt = null,
    decimal ConstructionDrainPerSecond = 0m);
