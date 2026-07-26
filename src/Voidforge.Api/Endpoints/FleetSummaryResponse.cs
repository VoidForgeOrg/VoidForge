using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

// Consumed by Task 5's list endpoints (#48).
public sealed record FleetSummaryResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId, DateTimeOffset AssembledAt, int ShipCount);
