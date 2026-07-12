using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetEnergyTests
{
    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, DateTimeOffset.UtcNow));
        return planet;
    }

    private static void Place(Planet planet, params BuildingType[] types)
    {
        var at = DateTimeOffset.UtcNow;
        foreach (var type in types)
        {
            planet.Apply(new BuildingPlaced(type, at));
        }
    }

    [Fact]
    public void EmptyPlanetHasNoEnergyAndFullProductivity()
    {
        var planet = CreateColonizedPlanet();

        Assert.Equal(0m, planet.GetEnergyGenerationMw());
        Assert.Equal(0m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());
    }

    [Fact]
    public void GeneratorOnlyGivesGenerationWithoutConsumption()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, BuildingType.Generator);

        Assert.Equal(100m, planet.GetEnergyGenerationMw());
        Assert.Equal(0m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());
    }

    [Fact]
    public void ExactlyBalancedLoadKeepsFullProductivity()
    {
        // Generator 100 MW vs 5 Drills x 20 MW = 100 MW.
        var planet = CreateColonizedPlanet();
        Place(planet, BuildingType.Generator, BuildingType.Drill, BuildingType.Drill,
            BuildingType.Drill, BuildingType.Drill, BuildingType.Drill);

        Assert.Equal(100m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());
    }

    [Fact]
    public void OverloadScalesProductivityProportionally()
    {
        // Generator 100 MW vs 4 Drills (80) + Refinery (30) = 110 MW.
        var planet = CreateColonizedPlanet();
        Place(planet, BuildingType.Generator, BuildingType.Drill, BuildingType.Drill,
            BuildingType.Drill, BuildingType.Drill, BuildingType.Refinery);

        Assert.Equal(110m, planet.GetEnergyConsumptionMw());
        Assert.Equal(100m / 110m, planet.GetProductivityMultiplier());
    }

    [Fact]
    public void ConsumersWithoutGeneratorGetZeroProductivity()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, BuildingType.Drill);

        Assert.Equal(0m, planet.GetProductivityMultiplier());
    }
}
