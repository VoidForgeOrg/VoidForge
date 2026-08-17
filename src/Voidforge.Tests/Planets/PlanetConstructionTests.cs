using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

[Trait("Category", "Unit")]
public sealed class PlanetConstructionTests
{
    private static Planet Homeworld(DateTimeOffset at)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));
        // Seed the homeworld operationally (bypasses construction), like registration does.
        planet.Apply(new BuildingPlaced(BuildingType.Drill, at));
        planet.Apply(new BuildingPlaced(BuildingType.Refinery, at));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        return planet;
    }

    [Fact]
    public void StartConstructionProducesEventWithComputedDrainAndCompletion()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        var started = planet.StartConstruction(BuildingType.Drill, now, ingotCost: 300m, buildDurationSeconds: 60m);

        Assert.Equal(3, started.SlotIndex);                 // 4th slot (0-based), after the 3 seeded
        Assert.Equal(BuildingType.Drill, started.BuildingType);
        Assert.Equal(now, started.StartedAt);
        Assert.Equal(now.AddSeconds(60), started.CompletesAt);
        Assert.Equal(5m, started.DrainPerSecond);           // 300 / 60
        Assert.Equal(3, planet.Buildings.Count);            // pure: no mutation before Apply
    }

    [Fact]
    public void StartConstructionThrowsWhenSlotsFull()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        // Fill the remaining 3 slots (slot count 6) as operational.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, now));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, now));
        planet.Apply(new BuildingPlaced(BuildingType.Generator, now));

        Assert.Throws<NoFreeSlotsException>(
            () => planet.StartConstruction(BuildingType.Drill, now, 300m, 60m));
    }

    [Fact]
    public void ApplyBuildingConstructionStartedAddsUnderConstructionSlotAndDrainsIngots()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var ingotRateBefore = planet.IronIngot.Rate;        // homeworld: +10/s

        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);

        var slot = planet.Buildings[started.SlotIndex];
        Assert.Equal(BuildingStatus.UnderConstruction, slot.Status);
        Assert.Equal(now.AddSeconds(60), slot.CompletesAt);
        // Construction drain subtracts from ingot rate; NOT scaled by m. 10 - 5 = 5.
        Assert.Equal(ingotRateBefore - 5m, planet.IronIngot.Rate);
        // The under-construction drill does NOT yet extract ore.
        // Homeworld: Drill inflow 10 - Refinery consumption 5 = net 5 (unchanged by construction start).
        Assert.Equal(5m, planet.IronOre.Rate);              // unchanged (homeworld drill only)
    }

    [Fact]
    public void CompleteBuildingFlipsToOperationalAndStartsEffects()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);

        var completedAt = started.CompletesAt;
        var events = planet.CompleteBuilding(started.SlotIndex, completedAt);
        Assert.Single(events);
        Assert.IsType<BuildingCompleted>(events[0]);
        planet.Apply((BuildingCompleted)events[0]);

        var slot = planet.Buildings[started.SlotIndex];
        Assert.Equal(BuildingStatus.Operational, slot.Status);
        Assert.Null(slot.CompletesAt);
        // Two operational drills now: ore rate 15 (inflow 20 - refinery consumption 5);
        // construction drain gone: ingots back to +10.
        Assert.Equal(15m, planet.IronOre.Rate);
        Assert.Equal(10m, planet.IronIngot.Rate);
    }

    [Fact]
    public void CompleteBuildingIsStaleNoOpWhenTimeDoesNotMatch()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);

        // Wrong completion time => stale => no events.
        var events = planet.CompleteBuilding(started.SlotIndex, started.CompletesAt.AddSeconds(1));
        Assert.Empty(events);
    }

    [Fact]
    public void CompleteBuildingIsStaleNoOpWhenAlreadyOperational()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);
        planet.Apply((BuildingCompleted)planet.CompleteBuilding(started.SlotIndex, started.CompletesAt)[0]);

        // Second delivery of the same completion => already Operational => no events.
        var again = planet.CompleteBuilding(started.SlotIndex, started.CompletesAt);
        Assert.Empty(again);
    }

    [Fact]
    public void CompleteBuildingIsNoOpForOutOfRangeSlot()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        Assert.Empty(planet.CompleteBuilding(99, now));
    }

    [Fact]
    public void CancelConstructionTombstonesSlotAndRemovesDrain()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);
        // Homeworld base ingot rate is +10/s; the construction drain (5/s) drops it to +5/s.
        Assert.Equal(5m, planet.IronIngot.Rate);

        var events = planet.CancelConstruction(started.SlotIndex, now);
        var cancelled = Assert.IsType<BuildingConstructionCancelled>(Assert.Single(events));
        Assert.Equal(started.SlotIndex, cancelled.SlotIndex);
        planet.Apply(cancelled);

        var slot = planet.Buildings[started.SlotIndex];
        Assert.Equal(BuildingStatus.Cancelled, slot.Status);
        Assert.Null(slot.CompletesAt);
        Assert.Equal(0m, slot.ConstructionDrainPerSecond);
        // Drain gone: ingot rate rebases back to the homeworld's +10/s.
        Assert.Equal(10m, planet.IronIngot.Rate);
    }

    [Fact]
    public void CancelledSlotIndexIsNeverReused()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);   // slots 0,1,2 operational
        var first = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        Assert.Equal(3, first.SlotIndex);
        planet.Apply(first);

        planet.Apply((BuildingConstructionCancelled)planet.CancelConstruction(first.SlotIndex, now)[0]);
        Assert.Equal(BuildingStatus.Cancelled, planet.Buildings[3].Status);

        // A fresh construction must claim the raw list length (4), NEVER reuse the tombstoned 3.
        var second = planet.StartConstruction(BuildingType.Refinery, now, 300m, 60m);
        Assert.Equal(planet.Buildings.Count, second.SlotIndex);   // = 4 (raw, monotonic id)
        Assert.Equal(4, second.SlotIndex);
        Assert.NotEqual(first.SlotIndex, second.SlotIndex);
        planet.Apply(second);

        // The cancelled slot stays a tombstone; the new slot is the one under construction.
        Assert.Equal(BuildingStatus.Cancelled, planet.Buildings[3].Status);
        Assert.Equal(BuildingStatus.UnderConstruction, planet.Buildings[4].Status);
    }

    [Fact]
    public void CancellingFreesSlotForNewPlacement()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);   // 3 live, slot count 6
        for (var i = 0; i < 3; i++)    // fill the remaining 3 slots via construction => 6 live
        {
            planet.Apply(planet.StartConstruction(BuildingType.Generator, now, 300m, 60m));
        }

        // All six slots are live (LiveBuildingCount == BuildingSlotCount) — placement is rejected.
        Assert.Throws<NoFreeSlotsException>(
            () => planet.StartConstruction(BuildingType.Drill, now, 300m, 60m));

        // Cancelling one under-construction slot frees a slot (LiveBuildingCount drops to 5).
        planet.Apply((BuildingConstructionCancelled)planet.CancelConstruction(5, now)[0]);

        // Placement now succeeds and claims a fresh index (6), never the cancelled slot (5).
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        Assert.Equal(planet.Buildings.Count, started.SlotIndex);   // = 6 (raw list length)
        Assert.NotEqual(5, started.SlotIndex);
    }

    [Fact]
    public void CompleteBuildingIsNoOpOnCancelledTombstone()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);
        var started = planet.StartConstruction(BuildingType.Drill, now, 300m, 60m);
        planet.Apply(started);
        planet.Apply((BuildingConstructionCancelled)planet.CancelConstruction(started.SlotIndex, now)[0]);

        // An in-flight CompleteBuildingConstruction for the now-cancelled slot at its old CompletesAt
        // finds the tombstone (status Cancelled, not UnderConstruction) and no-ops.
        Assert.Empty(planet.CompleteBuilding(started.SlotIndex, started.CompletesAt));
    }

    [Fact]
    public void CancelConstructionIsNoOpForOutOfRangeOrNonConstructingSlot()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = Homeworld(now);

        Assert.Empty(planet.CancelConstruction(99, now));   // out of range
        Assert.Empty(planet.CancelConstruction(0, now));    // slot 0 is a seeded Operational building
    }
}
