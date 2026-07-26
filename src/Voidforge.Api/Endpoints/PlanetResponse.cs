using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record PlanetResponse(
    Guid Id,
    string Name,
    Guid SolarSystemId,
    Guid? OwnerId,
    long IronOrePool,
    int BuildingSlotCount,
    decimal X,
    decimal Y,
    decimal Z,
    ResourcePoolResponse IronOre,
    ResourcePoolResponse IronIngot,
    EnergyResponse Energy,
    int ShipCount,
    int ActiveBuilds,
    int QueueLength,
    IReadOnlyList<BuildingSlotResponse> Buildings)
{
    public static PlanetResponse From(Planet planet, DateTimeOffset now) => new(
        planet.Id,
        planet.Name,
        planet.SolarSystemId,
        planet.OwnerId,
        planet.IronOrePool,
        planet.BuildingSlotCount,
        planet.X,
        planet.Y,
        planet.Z,
        new ResourcePoolResponse(
            planet.IronOre.GetCurrentValue(now),
            planet.IronOre.Rate,
            planet.IronOre.StorageCapacity),
        new ResourcePoolResponse(
            planet.IronIngot.GetCurrentValue(now),
            planet.IronIngot.Rate,
            planet.IronIngot.StorageCapacity),
        new EnergyResponse(
            planet.GetEnergyGenerationMw(),
            planet.GetEnergyConsumptionMw(),
            planet.GetProductivityMultiplier()),
        planet.Ships.Count,
        planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active),
        planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Queued),
        [.. planet.Buildings.Select(b => new BuildingSlotResponse(b.Type, b.Status, b.CompletesAt))]);
}
