using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record FleetResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId,
    DateTimeOffset AssembledAt, IReadOnlyList<FleetShipResponse> Ships,
    Guid? OriginPlanetId, Guid? DestinationPlanetId, MissionType? Mission,
    DateTimeOffset? DepartedAt, DateTimeOffset? ArrivesAt,
    decimal CargoIronOre, decimal CargoIronIngot, decimal CargoCapacity)
{
    // capacityOf keeps the response DTO config-free like Fleet.GetCargoCapacity itself
    // (#50) — callers inject t => balance.Ships.For(t).CargoCapacity.
    public static FleetResponse From(Fleet fleet, Func<ShipType, decimal> capacityOf) => new(
        fleet.Id, fleet.OwnerId, fleet.Status, fleet.LocationPlanetId, fleet.AssembledAt,
        fleet.Ships.Select(s => new FleetShipResponse(s.Id, s.Type, s.CompletedAt)).ToList(),
        fleet.OriginPlanetId, fleet.DestinationPlanetId, fleet.Mission, fleet.DepartedAt, fleet.ArrivesAt,
        fleet.CargoIronOre, fleet.CargoIronIngot, fleet.GetCargoCapacity(capacityOf));
}
