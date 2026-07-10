using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record PlanetResponse(
    Guid Id,
    string Name,
    Guid SolarSystemId,
    Guid? OwnerId,
    long IronOrePool,
    int BuildingSlotCount,
    ResourcePoolResponse IronOre,
    ResourcePoolResponse IronIngot,
    EnergyResponse Energy,
    IReadOnlyList<BuildingSlotResponse> Buildings)
{
    public static PlanetResponse From(Planet planet, DateTimeOffset now) => new(
        planet.Id,
        planet.Name,
        planet.SolarSystemId,
        planet.OwnerId,
        planet.IronOrePool,
        planet.BuildingSlotCount,
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
        [.. planet.Buildings.Select(b => new BuildingSlotResponse(b.Type, b.Status, b.CompletesAt))]);
}
