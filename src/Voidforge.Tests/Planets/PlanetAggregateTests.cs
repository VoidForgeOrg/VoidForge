using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetAggregateTests
{
    [Fact]
    public void ApplyPlanetCreatedSetsAllProperties()
    {
        var planet = new Planet();
        var solarSystemId = Guid.NewGuid();

        planet.Apply(new PlanetCreated(
            Name: "Test Planet",
            SolarSystemId: solarSystemId,
            IronOrePool: 50000,
            BuildingSlotCount: 6,
            IronOreStorageCapacity: 10000,
            IronIngotStorageCapacity: 5000));

        Assert.Equal("Test Planet", planet.Name);
        Assert.Equal(solarSystemId, planet.SolarSystemId);
        Assert.Equal(50000, planet.IronOrePool);
        Assert.Equal(6, planet.BuildingSlotCount);
        Assert.Equal(0m, planet.IronOre.CheckpointValue);
        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Equal(10000m, planet.IronOre.StorageCapacity);
        Assert.Equal(0m, planet.IronIngot.CheckpointValue);
        Assert.Equal(0m, planet.IronIngot.Rate);
        Assert.Equal(5000m, planet.IronIngot.StorageCapacity);
    }

    [Fact]
    public void NewPlanetHasNullOwner()
    {
        var planet = new Planet();

        Assert.Null(planet.OwnerId);
    }

    [Fact]
    public void NewPlanetHasDefaultValues()
    {
        var planet = new Planet();

        Assert.Equal(Guid.Empty, planet.Id);
        Assert.Equal(string.Empty, planet.Name);
        Assert.Equal(Guid.Empty, planet.SolarSystemId);
        Assert.Null(planet.OwnerId);
        Assert.Equal(0, planet.IronOrePool);
        Assert.Equal(0, planet.BuildingSlotCount);
        Assert.Equal(0m, planet.IronOre.CheckpointValue);
        Assert.Equal(0m, planet.IronOre.StorageCapacity);
        Assert.Equal(0m, planet.IronIngot.CheckpointValue);
        Assert.Equal(0m, planet.IronIngot.StorageCapacity);
    }

    [Fact]
    public void ApplyPlanetColonizedSetsOwnerAndResources()
    {
        var planet = new Planet();
        var solarSystemId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var colonizedAt = DateTimeOffset.UtcNow;

        planet.Apply(new PlanetCreated(
            Name: "Test Planet",
            SolarSystemId: solarSystemId,
            IronOrePool: 50000,
            BuildingSlotCount: 6,
            IronOreStorageCapacity: 10000,
            IronIngotStorageCapacity: 5000));

        planet.Apply(new PlanetColonized(
            OwnerId: ownerId,
            IronOreStored: 500,
            IronIngotStored: 100,
            ColonizedAt: colonizedAt));

        Assert.Equal(ownerId, planet.OwnerId);
        Assert.Equal(500m, planet.IronOre.CheckpointValue);
        Assert.Equal(colonizedAt, planet.IronOre.CheckpointTime);
        Assert.Equal(10000m, planet.IronOre.StorageCapacity);
        Assert.Equal(100m, planet.IronIngot.CheckpointValue);
        Assert.Equal(colonizedAt, planet.IronIngot.CheckpointTime);
        Assert.Equal(5000m, planet.IronIngot.StorageCapacity);
    }

    [Fact]
    public void ApplyBuildingPlacedDrillSetsIronOreRate()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Drill, 10, placedAt));

        Assert.Equal(10m, planet.IronOre.Rate);
        Assert.Single(planet.Buildings);
        Assert.Equal(BuildingType.Drill, planet.Buildings[0].Type);
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[0].Status);
    }

    [Fact]
    public void ApplyBuildingPlacedMultipleDrillsAreAdditive()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Drill, 10, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, 15, placedAt));

        Assert.Equal(25m, planet.IronOre.Rate);
        Assert.Equal(2, planet.Buildings.Count);
    }

    [Fact]
    public void ApplyBuildingPlacedDrillCheckpointsAccumulatedOreBeforeRateChange()
    {
        var colonizedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, colonizedAt));

        // First drill runs for 10s at 10/sec, accumulating 100 ore. A second drill is then placed.
        planet.Apply(new BuildingPlaced(BuildingType.Drill, 10, colonizedAt));
        var secondPlacedAt = colonizedAt.AddSeconds(10);
        planet.Apply(new BuildingPlaced(BuildingType.Drill, 10, secondPlacedAt));

        // 500 + 10/sec * 10s = 600 locked in at the rate change.
        Assert.Equal(600m, planet.IronOre.CheckpointValue);
        Assert.Equal(secondPlacedAt, planet.IronOre.CheckpointTime);
        Assert.Equal(20m, planet.IronOre.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedNonDrillDoesNotChangeIronOreRate()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Refinery, 0, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, 0, placedAt));

        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Equal(2, planet.Buildings.Count);
    }

    [Fact]
    public void CheckpointAllResourcesUpdatesBaselines()
    {
        var planet = new Planet();
        var colonizedAt = DateTimeOffset.UtcNow;

        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, colonizedAt));

        // Manually set a rate to verify checkpoint math
        planet.IronOre = planet.IronOre with { Rate = 10 };

        var checkpointTime = colonizedAt.AddSeconds(5);
        planet.CheckpointAllResources(checkpointTime);

        Assert.Equal(550m, planet.IronOre.CheckpointValue);
        Assert.Equal(checkpointTime, planet.IronOre.CheckpointTime);
    }
}
