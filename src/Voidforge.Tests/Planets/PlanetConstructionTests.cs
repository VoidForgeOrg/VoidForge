using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetConstructionTests
{
    private static Planet Homeworld(DateTimeOffset at)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000));
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
}
