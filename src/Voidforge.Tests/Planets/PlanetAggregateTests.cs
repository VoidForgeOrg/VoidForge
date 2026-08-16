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
            IronIngotStorageCapacity: 5000,
            X: 0m,
            Y: 0m,
            Z: 0m));

        Assert.Equal("Test Planet", planet.Name);
        Assert.Equal(solarSystemId, planet.SolarSystemId);
        // The finite deposit is seeded full, not yet draining: value and capacity both 50000.
        Assert.Equal(50000m, planet.IronOreDeposit.CheckpointValue);
        Assert.Equal(0m, planet.IronOreDeposit.Rate);
        Assert.Equal(50000m, planet.IronOreDeposit.StorageCapacity);
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
        Assert.Equal(0m, planet.IronOreDeposit.CheckpointValue);
        Assert.Equal(0m, planet.IronOreDeposit.StorageCapacity);
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
            IronIngotStorageCapacity: 5000,
            X: 0m,
            Y: 0m,
            Z: 0m));

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
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
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
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
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
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
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
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));

        Assert.Equal(0m, planet.IronOre.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedOverloadRescalesExistingDrillRates()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        // Generator (100) + 4 Drills (80) + Refinery (30) => consumption 110, m = 100/110.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, placedAt));

        // Net ore rate = (drill inflow 40 - refinery consumption 5) x m.
        var m = 100m / 110m;
        Assert.Equal((40m - 5m) * m, planet.IronOre.Rate);
        // Ingots = 2 x effective consumption; effective = min(refinery demand 5, inflow 40) x m = 5m.
        Assert.Equal(BuildingSpecs.RefineryIngotOutputFactor * 5m * m, planet.IronIngot.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedRefineryConsumesOreAndProducesIngots()
    {
        var at = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));

        // Generator + Drill (inflow 10) + Refinery (demand 5), m = 1.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));

        Assert.Equal(5m, planet.IronOre.Rate);    // 10 inflow - 5 consumed
        Assert.Equal(10m, planet.IronIngot.Rate); // 2 x 5 consumed
    }

    [Fact]
    public void ApplyBuildingPlacedRefineryDemandDrawsStoredBufferWhenInflowShort()
    {
        var at = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 7, 10000, 5000, 0m, 0m, 0m));
        // Seeds a NON-empty IronOre buffer (500 stored), so the buffer-drain branch applies (#70).
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));

        // 2 Generators (200 MW) + Drill (20 MW) + 3 Refineries (90 MW) => m = 1.
        // Drill inflow 10; 3-refinery demand 15 > inflow 10, m = 1.
        // The stored buffer has ore, so refineries run at FULL demand (#70): they draw the buffer, so
        // net ore = inflow 10 - demand 15 = -5 (buffer draining), and ingots = 2 x demand 15 = 30.
        // (Under the old Phase-3 clamp this asserted net ore 0 and ingots 20.)
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));

        Assert.Equal(-5m, planet.IronOre.Rate);
        Assert.Equal(30m, planet.IronIngot.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedRefineryWithoutDrillDrainsStoredBuffer()
    {
        var at = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        // Seeds a NON-empty IronOre buffer (500 stored).
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));

        // Generator + Refinery, no Drill: zero inflow, but the stored buffer has ore, so the refinery
        // draws it down (#70). Net ore = 0 - demand 5 = -5 (buffer draining); ingots = 2 x 5 = 10.
        // (Under the old Phase-3 clamp a drill-less refinery was idle: net ore 0, ingots 0.)
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));

        Assert.Equal(-5m, planet.IronOre.Rate);
        Assert.Equal(10m, planet.IronIngot.Rate);
    }

    [Fact]
    public void ApplyBuildingPlacedNonDrillDoesNotChangeIronOreRate()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, placedAt));

        // A Generator neither produces nor consumes ore, so it leaves the ore rate untouched (0).
        // (A Refinery is a different story post-#70 — it drains the stored buffer; that case is
        // covered by ApplyBuildingPlacedRefineryWithoutDrillDrainsStoredBuffer.)
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));

        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Single(planet.Buildings);
    }

    [Fact]
    public void PlaceBuildingReturnsEventWhenSlotAvailable()
    {
        var placedAt = DateTimeOffset.UtcNow;
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 2, 10000, 5000, 0m, 0m, 0m));

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
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 2, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, placedAt));

        Assert.Throws<NoFreeSlotsException>(() => planet.PlaceBuilding(BuildingType.Drill, placedAt));
    }

    [Fact]
    public void CheckpointAllResourcesUpdatesBaselines()
    {
        var planet = new Planet();
        var colonizedAt = DateTimeOffset.UtcNow;

        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, colonizedAt));

        // Manually set a rate to verify checkpoint math
        planet.IronOre = planet.IronOre with { Rate = 10 };

        var checkpointTime = colonizedAt.AddSeconds(5);
        planet.CheckpointAllResources(checkpointTime);

        Assert.Equal(550m, planet.IronOre.CheckpointValue);
        Assert.Equal(checkpointTime, planet.IronOre.CheckpointTime);
    }
}
