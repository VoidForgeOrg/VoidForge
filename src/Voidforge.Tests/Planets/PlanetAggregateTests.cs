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

        // A powered drill extracts at full rate (Generator 100 MW >= Drill 20 MW).
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));

        Assert.Equal(BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill), planet.IronOre.Rate);
        Assert.Equal(2, planet.Buildings.Count);
        Assert.Equal(BuildingType.Drill, planet.Buildings[1].Type);
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[1].Status);
    }

    [Fact]
    public void ApplyBuildingPlacedMultipleDrillsAreAdditive()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));

        Assert.Equal(BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill) * 2, planet.IronOre.Rate);
        Assert.Equal(3, planet.Buildings.Count);
    }

    [Fact]
    public void ApplyBuildingPlacedDrillCheckpointsAccumulatedOreBeforeRateChange()
    {
        var colonizedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, colonizedAt));

        var rate = BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill);

        // First powered drill runs for 10s, accumulating rate*10 ore. Then a second drill lands.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, colonizedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, colonizedAt));
        var secondPlacedAt = colonizedAt.AddSeconds(10);
        planet.Apply(new BuildingPlaced(BuildingType.Drill, secondPlacedAt));

        // 500 + rate * 10s locked in at the rate change.
        Assert.Equal(500m + (rate * 10m), planet.IronOre.CheckpointValue);
        Assert.Equal(secondPlacedAt, planet.IronOre.CheckpointTime);
        Assert.Equal(rate * 2, planet.IronOre.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedDrillWithoutGeneratorExtractsNothing()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));

        Assert.Equal(0m, planet.IronOre.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedOverloadRescalesExistingDrillRates()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        // Generator (100) + 4 Drills (80) + Refinery (30) => consumption 110, m = 100/110.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, placedAt));

        var expectedRate = BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill) * 4 * (100m / 110m);
        Assert.Equal(expectedRate, planet.IronOre.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedNonDrillDoesNotChangeIronOreRate()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Refinery, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));

        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Equal(2, planet.Buildings.Count);
    }

    [Fact]
    public void PlaceBuildingReturnsEventWhenSlotAvailable()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 2, 10000, 5000));

        var @event = planet.PlaceBuilding(BuildingType.Drill, placedAt);

        Assert.Equal(BuildingType.Drill, @event.BuildingType);
        Assert.Equal(placedAt, @event.PlacedAt);
        // PlaceBuilding validates and produces the event; it does not mutate until applied.
        Assert.Empty(planet.Buildings);
    }

    [Fact]
    public void PlaceBuildingThrowsWhenSlotsFull()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 2, 10000, 5000));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));

        Assert.Throws<NoFreeSlotsException>(() => planet.PlaceBuilding(BuildingType.Drill, placedAt));
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
