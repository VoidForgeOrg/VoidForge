namespace Voidforge.Api.Domain;

// CompletesAt and ConstructionDrainPerSecond are populated while UnderConstruction (both null/0 for
// Operational slots, including homeworld-seeded buildings) and RETAINED while ConstructionHalted so
// resume can recompute the remaining work (#83). HaltedAt is set only while ConstructionHalted — the
// instant the build paused on zero ingots — and cleared on resume.
public sealed record BuildingSlot(
    BuildingType Type,
    BuildingStatus Status,
    DateTimeOffset? CompletesAt = null,
    decimal ConstructionDrainPerSecond = 0m,
    HaltReason? HaltReason = null,
    DateTimeOffset? HaltedAt = null);
