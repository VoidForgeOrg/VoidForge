using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record FleetResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId,
    DateTimeOffset AssembledAt, IReadOnlyList<FleetShipResponse> Ships,
    Guid? OriginPlanetId, Guid? DestinationPlanetId, MissionType? Mission,
    DateTimeOffset? DepartedAt, DateTimeOffset? ArrivesAt)
{
    public static FleetResponse From(Fleet fleet) => new(
        fleet.Id, fleet.OwnerId, fleet.Status, fleet.LocationPlanetId, fleet.AssembledAt,
        fleet.Ships.Select(s => new FleetShipResponse(s.Id, s.Type, s.CompletedAt)).ToList(),
        fleet.OriginPlanetId, fleet.DestinationPlanetId, fleet.Mission, fleet.DepartedAt, fleet.ArrivesAt);
}
