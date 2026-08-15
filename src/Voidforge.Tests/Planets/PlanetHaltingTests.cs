using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetHaltingTests
{
    // Fixed base time so checkpoint/deadline math is deterministic (no DateTimeOffset.UtcNow).
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Storage caps come from PlanetCreated: IronOre 10000, IronIngot 5000.
    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, Base));
        return planet;
    }

    private static void Place(Planet planet, DateTimeOffset at, params BuildingType[] types)
    {
        foreach (var type in types)
        {
            planet.Apply(new BuildingPlaced(type, at));
        }
    }

    // Drive the ore pool to its storage cap without changing composition.
    private static void FillOreToCapacity(Planet planet, DateTimeOffset at) =>
        planet.IronOre = planet.IronOre with { CheckpointValue = planet.IronOre.StorageCapacity, CheckpointTime = at };

    // (a) An Operational Drill whose IronOre output pool is at capacity halts.
    [Fact]
    public void EvaluateStorageHaltsEmitsBuildingHaltedForDrillWhenOreFull()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill);
        FillOreToCapacity(planet, Base);

        var events = planet.EvaluateStorageHalts(Base);

        var halt = Assert.IsType<BuildingHalted>(Assert.Single(events));
        Assert.Equal(1, halt.SlotIndex); // Generator is slot 0, Drill slot 1.
        Assert.Equal(HaltReason.OutputStorageFull, halt.Reason);
        Assert.Equal(Base, halt.At);
    }

    // (a') No halt while the pool has headroom.
    [Fact]
    public void EvaluateStorageHaltsEmitsNothingWhenOreBelowCapacity()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill);

        Assert.Empty(planet.EvaluateStorageHalts(Base));
    }

    // (b) Apply(BuildingHalted) marks the slot Halted+reason, drops ore inflow to 0, and the
    // halted Drill now draws only 0.05 * 20 = 1 MW.
    [Fact]
    public void ApplyBuildingHaltedSetsStatusDropsOreRateAndDrawsFivePercent()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill);
        FillOreToCapacity(planet, Base);

        var before = planet.GetEnergyConsumptionMw(); // Drill 20 + Generator 0.
        Assert.Equal(20m, before);
        Assert.Equal(BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill), planet.IronOre.Rate);

        planet.Apply(new BuildingHalted(1, HaltReason.OutputStorageFull, Base));

        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.OutputStorageFull, planet.Buildings[1].HaltReason);
        Assert.Equal(0m, planet.IronOre.Rate); // Halted Drill left the Operational set.

        var after = planet.GetEnergyConsumptionMw();
        Assert.Equal(1m, after); // 0.05 * 20 MW.
        Assert.Equal(19m, before - after);
    }

    // (c) EvaluateStorageResumes emits BuildingResumed once the pool frees up, and
    // Apply(BuildingResumed) restores Operational status and the ore rate.
    [Fact]
    public void EvaluateAndApplyResumeRestoresOperationalDrillWhenOreFrees()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill);
        FillOreToCapacity(planet, Base);
        planet.Apply(new BuildingHalted(1, HaltReason.OutputStorageFull, Base));

        // No resume while the pool is still full.
        Assert.Empty(planet.EvaluateStorageResumes(Base));

        // Free the pool (e.g. cargo loaded away) — value drops below capacity.
        planet.IronOre = planet.IronOre with { CheckpointValue = 9000m };
        var resumeAt = Base.AddSeconds(60);

        var events = planet.EvaluateStorageResumes(resumeAt);
        var resumed = Assert.IsType<BuildingResumed>(Assert.Single(events));
        Assert.Equal(1, resumed.SlotIndex);

        planet.Apply(new BuildingResumed(1, resumeAt));

        Assert.Equal(BuildingStatus.Operational, planet.Buildings[1].Status);
        Assert.Null(planet.Buildings[1].HaltReason);
        Assert.Equal(BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill), planet.IronOre.Rate);
    }

    // (d) PredictStorageDeadlines returns (capacity - current) / rate for a filling pool and
    // nothing for a full pool or a zero/negative-rate pool.
    [Fact]
    public void PredictStorageDeadlinesReturnsFillInstantOnlyForPositiveRatePoolsBelowCapacity()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill); // ore rate 10, ingot rate 0.
        var now = Base.AddSeconds(30);
        planet.IronOre = planet.IronOre with { CheckpointValue = 9000m, CheckpointTime = now, Rate = 10m };

        var deadlines = planet.PredictStorageDeadlines(now);

        // Only IronOre qualifies: ingot rate is 0 (no Refinery), so it is skipped.
        var deadline = Assert.Single(deadlines);
        Assert.Equal(ResourceType.IronOre, deadline.Resource);
        Assert.Equal(now.AddSeconds(100), deadline.At); // (10000 - 9000) / 10 = 100s.

        // A full pool yields no deadline despite its positive rate.
        FillOreToCapacity(planet, now);
        Assert.Empty(planet.PredictStorageDeadlines(now));

        // A planet with no producers has zero rates → no deadlines at all.
        Assert.Empty(CreateColonizedPlanet().PredictStorageDeadlines(now));
    }

    // (e) Halting a Drill frees energy that lifts the productivity multiplier — the energy
    // cascade collapses into RebaseRates' single re-derivation.
    [Fact]
    public void ApplyBuildingHaltedLiftsProductivityMultiplierInOneRederivation()
    {
        var planet = CreateColonizedPlanet();
        // Generator 100 MW vs 4 Drills (80) + Refinery (30) = 110 MW → overloaded, m = 100/110.
        Place(planet, Base, BuildingType.Generator, BuildingType.Drill, BuildingType.Drill,
            BuildingType.Drill, BuildingType.Drill, BuildingType.Refinery);
        Assert.Equal(100m / 110m, planet.GetProductivityMultiplier());

        // One Drill (slot 1) halts: operational load drops to 3 Drills (60) + Refinery (30) = 90,
        // plus the halted Drill's 5% draw (1 MW) = 91 < 100 generation → m = 1.
        planet.Apply(new BuildingHalted(1, HaltReason.OutputStorageFull, Base));

        Assert.Equal(91m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());
    }
}
