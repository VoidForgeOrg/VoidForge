using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

// EtaCompletionUtc is the scheduled completion time for UnderConstruction slots (null for
// Operational). Lazy — read straight from slot state at request time.
public sealed record BuildingSlotResponse(
    BuildingType Type,
    BuildingStatus Status,
    DateTimeOffset? EtaCompletionUtc);
