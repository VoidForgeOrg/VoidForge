using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

[Trait("Category", "Unit")]
public sealed class PlanetDemolitionTests
{
    private static Planet Homeworld(DateTimeOffset at)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));
        // Seed the homeworld operationally (bypasses construction), like registration does:
        // slot 0 Drill, 1 Refinery, 2 Generator.
        planet.Apply(new BuildingPlaced(BuildingType.Drill, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        return planet;
    }

    // Colonized planet with the caller-supplied operational composition (each BuildingPlaced lands
    // Operational). Mirrors PlanetEnergyTests so the overload numbers line up.
    private static Planet ColonizedWith(DateTimeOffset at, params BuildingType[] types)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));
        foreach (var type in types)
        {
            planet.Apply(new BuildingPlaced(type, at));
        }

        return planet;
    }

    [Fact]
    public void StartDemolitionOnOperationalProducesEvent()
    {
        const decimal demolitionSeconds = 600m;
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        var events = planet.StartDemolition(0, now, demolitionSeconds);

        var started = Assert.IsType<BuildingDemolitionStarted>(Assert.Single(events));
        Assert.Equal(0, started.SlotIndex);
        Assert.Equal(now, started.At);
        Assert.Equal(now.AddSeconds((double)demolitionSeconds), started.CompletesAt);
        // Pure: no mutation before Apply — the slot is still Operational.
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[0].Status);
    }

    [Fact]
    public void StartDemolitionIsNoOpForOutOfRangeSlot()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        Assert.Empty(planet.StartDemolition(99, now, 600m));
        Assert.Empty(planet.StartDemolition(-1, now, 600m));
    }

    [Fact]
    public void StartDemolitionIsNoOpForUnderConstruction()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);

        // Can't demolish an under-construction slot — cancel it instead.
        Assert.Empty(planet.StartDemolition(started.SlotIndex, now, 600m));
    }

    [Fact]
    public void StartDemolitionIsNoOpForAlreadyDemolishingSlot()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);

        Assert.Equal(BuildingStatus.Demolishing, planet.Buildings[0].Status);
        Assert.Empty(planet.StartDemolition(0, now, 600m));
    }

    [Fact]
    public void StartDemolitionIsNoOpForTombstones()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        // Cancelled tombstone: start + cancel a construction.
        var uc = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(uc);
        planet.Apply((BuildingConstructionCancelled)planet.CancelConstruction(uc.SlotIndex, now)[0]);
        Assert.Equal(BuildingStatus.Cancelled, planet.Buildings[uc.SlotIndex].Status);
        Assert.Empty(planet.StartDemolition(uc.SlotIndex, now, 600m));

        // Demolished tombstone: demolish slot 0 to completion.
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);
        var completesAt = planet.Buildings[0].CompletesAt!.Value;
        planet.Apply((BuildingDemolished)planet.CompleteDemolition(0, completesAt)[0]);
        Assert.Equal(BuildingStatus.Demolished, planet.Buildings[0].Status);
        Assert.Empty(planet.StartDemolition(0, now, 600m));
    }

    [Fact]
    public void ApplyBuildingDemolitionStartedShutsBuildingDownImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        // Homeworld baseline: Drill 20 + Refinery 30 = 50 MW draw; ore rate +5 (inflow 10 − refinery 5).
        Assert.Equal(50m, planet.GetEnergyConsumptionMw());
        Assert.Equal(5m, planet.IronOre.Rate);
        Assert.Equal(-10m, planet.IronOreDeposit.Rate);

        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);

        var slot = planet.Buildings[0];
        Assert.Equal(BuildingStatus.Demolishing, slot.Status);
        Assert.Null(slot.HaltReason);
        Assert.Equal(0m, slot.ConstructionDrainPerSecond);
        Assert.Equal(now.AddSeconds(600), slot.CompletesAt);

        // Immediate shutdown: the Drill leaves the Operational set — it draws nothing (consumption
        // drops to the Refinery's 30 MW) and produces nothing (deposit drain → 0, so ore inflow is 0
        // and the buffer-fed Refinery now pulls the ore rate negative to −5).
        Assert.Equal(30m, planet.GetEnergyConsumptionMw());
        Assert.Equal(0m, planet.IronOreDeposit.Rate);
        Assert.Equal(-5m, planet.IronOre.Rate);
    }

    [Fact]
    public void DemolishingAConsumerFreesEnergyAndResolvesOverloadInTheSameCommit()
    {
        var now = DateTimeOffset.UtcNow;
        // Overloaded: Generator 100 MW vs 4 Drills (80) + Refinery (30) = 110 MW. Slots: 0 Generator,
        // 1-4 Drills, 5 Refinery.
        var planet = ColonizedWith(
            now,
            BuildingType.Generator,
            BuildingType.Drill,
            BuildingType.Drill,
            BuildingType.Drill,
            BuildingType.Drill,
            BuildingType.Refinery);

        Assert.Equal(110m, planet.GetEnergyConsumptionMw());
        Assert.Equal(100m / 110m, planet.GetProductivityMultiplier());

        // Demolish the Refinery (slot 5): its 30 MW draw is freed immediately, dropping demand to 80 MW
        // (< the 100 MW generation), so the productivity multiplier recovers to 1 in this one commit —
        // the D9 "energy freed → overload resolves" cascade resolves inside RebaseRates.
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(5, now, 600m)[0]);

        Assert.Equal(BuildingStatus.Demolishing, planet.Buildings[5].Status);
        Assert.Equal(80m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());
    }

    [Fact]
    public void CompleteDemolitionValidatesOnArrival()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);
        var completesAt = planet.Buildings[0].CompletesAt!.Value;

        // Wrong completion time => stale => no events.
        Assert.Empty(planet.CompleteDemolition(0, completesAt.AddSeconds(1)));

        // Matching time => tombstone event.
        var events = planet.CompleteDemolition(0, completesAt);
        var done = Assert.IsType<BuildingDemolished>(Assert.Single(events));
        Assert.Equal(0, done.SlotIndex);
        Assert.Equal(completesAt, done.At);

        planet.Apply(done);
        Assert.Equal(BuildingStatus.Demolished, planet.Buildings[0].Status);
        Assert.Null(planet.Buildings[0].CompletesAt);
    }

    [Fact]
    public void CompleteDemolitionIsNoOpUnlessDemolishing()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        Assert.Empty(planet.CompleteDemolition(0, now));    // slot 0 is Operational, not Demolishing
        Assert.Empty(planet.CompleteDemolition(99, now));   // out of range
    }

    [Fact]
    public void CompleteDemolitionIsNoOpOnDemolishedTombstone()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);
        var completesAt = planet.Buildings[0].CompletesAt!.Value;
        planet.Apply((BuildingDemolished)planet.CompleteDemolition(0, completesAt)[0]);

        // A redelivered CompleteBuildingDemolition finds the tombstone (Demolished, not Demolishing)
        // and no-ops.
        Assert.Empty(planet.CompleteDemolition(0, completesAt));
    }

    [Fact]
    public void DemolishedSlotIsFreedForNewPlacement()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);   // 3 live, slot count 6
        for (var i = 0; i < 3; i++)    // fill the remaining 3 slots via construction => 6 live
        {
            planet.Apply(planet.StartConstruction(BuildingType.Generator, now, 300m, 60m));
        }

        // All six slots are live — placement is rejected.
        Assert.Throws<NoFreeSlotsException>(
            () => planet.StartConstruction(BuildingType.Drill, now, 300m, 60m));

        // Start demolishing slot 0: Demolishing still OCCUPIES the slot, so the planet stays full.
        planet.Apply((BuildingDemolitionStarted)planet.StartDemolition(0, now, 600m)[0]);
        Assert.Throws<NoFreeSlotsException>(
            () => planet.StartConstruction(BuildingType.Drill, now, 300m, 60m));

        // Completing the teardown tombstones slot 0 (Demolished) and frees the slot.
        var completesAt = planet.Buildings[0].CompletesAt!.Value;
        planet.Apply((BuildingDemolished)planet.CompleteDemolition(0, completesAt)[0]);
        Assert.Equal(BuildingStatus.Demolished, planet.Buildings[0].Status);

        // Placement now succeeds and claims a fresh index (6, the raw list length), NEVER reusing the
        // demolished slot 0.
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        Assert.Equal(planet.Buildings.Count, started.SlotIndex);   // = 6 (raw, monotonic id)
        Assert.Equal(6, started.SlotIndex);
        Assert.NotEqual(0, started.SlotIndex);
    }
}
