using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

// Lightweight per-fleet projection for the fleet-list endpoints (FleetEndpoints.GetOwnFleets,
// GetPlanetFleets, #48) — omits ships/cargo/mission detail, which lives on FleetResponse.
public sealed record FleetSummaryResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId, DateTimeOffset AssembledAt, int ShipCount);
