using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

// Ingot-consumer starvation (#83, Task 1): an in-flight UnderConstruction building pauses
// (ConstructionHalted) only when ingot production is zero AND the IronIngot buffer is empty, and
// resumes to UnderConstruction with a completion time pushed out by exactly the paused duration.
// Mirrors PlanetInputStarvationTests' fixed-base-time, direct-Apply style so checkpoint/deadline math
// is deterministic (no DateTimeOffset.UtcNow).
[Trait("Category", "Unit")]
public sealed class PlanetIngotStarvationTests
{
    private static readonly DateTimeOffset _base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Storage caps come from PlanetCreated: deposit 50000, IronOre 10000, IronIngot 5000. Colonization
    // seeds a NON-empty IronIngot buffer of 100, so the buffer-not-empty no-op branch is exercised.
    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 7, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, _base));
        return planet;
    }

    private static void Place(Planet planet, DateTimeOffset at, params BuildingType[] types)
    {
        foreach (var type in types)
        {
            planet.Apply(new BuildingPlaced(type, at));
        }
    }

    // Start a building construction and apply it — the slot becomes UnderConstruction with the derived
    // drain and completion time.
    private static void StartBuild(
        Planet planet, BuildingType type, DateTimeOffset at, decimal ingotCost, decimal durationSeconds)
    {
        planet.Apply(planet.StartConstruction(type, at, ingotCost, durationSeconds));
    }

    // Pin the stored IronIngot buffer to empty without changing composition (the Rate stays as
    // RebaseRates derived it, which is what the starvation check runs against). Mirrors EmptyOreBuffer.
    private static void EmptyIngotBuffer(Planet planet, DateTimeOffset at) =>
        planet.IronIngot = planet.IronIngot with { CheckpointValue = 0m, CheckpointTime = at };

    // (a) EvaluateIngotStarvation halts an UnderConstruction building when ingot production is 0 (no
    // operational refinery) AND the ingot buffer is empty.
    [Fact]
    public void EvaluateIngotStarvationHaltsBuildWhenZeroProductionAndEmptyBuffer()
    {
        var planet = CreateColonizedPlanet();
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m); // slot 0, drain 1/s.
        EmptyIngotBuffer(planet, _base);

        var events = planet.EvaluateIngotStarvation(_base);

        var halt = Assert.IsType<ConstructionHalted>(Assert.Single(events));
        Assert.Equal(0, halt.SlotIndex);
        Assert.Equal(_base, halt.At);
    }

    // (b) Apply(ConstructionHalted): status → ConstructionHalted, HaltedAt stamped, CompletesAt +
    // ConstructionDrainPerSecond preserved for resume, and the construction drain drops out of the
    // ingot rate.
    [Fact]
    public void ApplyConstructionHaltedStopsDrainAndCapturesState()
    {
        var planet = CreateColonizedPlanet();
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m);
        EmptyIngotBuffer(planet, _base);
        var completesAt = planet.Buildings[0].CompletesAt;
        Assert.Equal(-1m, planet.IronIngot.Rate); // production 0 - drain 1.

        var halt = Assert.IsType<ConstructionHalted>(Assert.Single(planet.EvaluateIngotStarvation(_base)));
        planet.Apply(halt);

        Assert.Equal(BuildingStatus.ConstructionHalted, planet.Buildings[0].Status);
        Assert.Equal(_base, planet.Buildings[0].HaltedAt);
        Assert.Equal(completesAt, planet.Buildings[0].CompletesAt); // preserved for resume.
        Assert.Equal(1m, planet.Buildings[0].ConstructionDrainPerSecond); // kept, harmless while halted.
        Assert.Equal(0m, planet.IronIngot.Rate); // drain dropped: no production, no under-construction drain.
    }

    // (c) Apply(ConstructionResumed) at a later time: status → UnderConstruction, CompletesAt pushed out
    // by exactly the paused duration (resumeAt + (originalCompletesAt - haltedAt)), HaltedAt cleared,
    // drain restored to the ingot rate.
    [Fact]
    public void ApplyConstructionResumedPushesCompletionOutByPausedDuration()
    {
        var planet = CreateColonizedPlanet();
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m);
        var originalCompletesAt = planet.Buildings[0].CompletesAt!.Value; // _base + 100s.
        EmptyIngotBuffer(planet, _base);

        var haltAt = _base.AddSeconds(30); // 70s of work remaining at the pause.
        var halt = Assert.IsType<ConstructionHalted>(Assert.Single(planet.EvaluateIngotStarvation(haltAt)));
        planet.Apply(halt);
        Assert.Equal(haltAt, planet.Buildings[0].HaltedAt);

        var resumeAt = _base.AddSeconds(200);
        planet.Apply(new ConstructionResumed(0, resumeAt));

        Assert.Equal(BuildingStatus.UnderConstruction, planet.Buildings[0].Status);
        Assert.Null(planet.Buildings[0].HaltedAt);
        // Remaining at halt = originalCompletesAt - haltAt = 70s; new completion = resumeAt + 70s.
        Assert.Equal(resumeAt + (originalCompletesAt - haltAt), planet.Buildings[0].CompletesAt);
        Assert.Equal(_base.AddSeconds(270), planet.Buildings[0].CompletesAt); // 200 + 70.
        Assert.Equal(-1m, planet.IronIngot.Rate); // drain restored: production 0 - drain 1.
    }

    // (d) No-op when ingot production > 0: a build draws from live refinery output even with an empty
    // buffer, so it is not starved.
    [Fact]
    public void EvaluateIngotStarvationEmitsNothingWhenProductionPositive()
    {
        var planet = CreateColonizedPlanet();
        // Generator + Drill (inflow 10) + Refinery (demand 5) → ingot production 10/s.
        Place(planet, _base, BuildingType.Generator, BuildingType.Drill, BuildingType.Refinery);
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m); // slot 3, under construction.
        EmptyIngotBuffer(planet, _base);

        Assert.Empty(planet.EvaluateIngotStarvation(_base));
    }

    // (d') No-op when the ingot buffer still has ingots, even with zero production: the build is draining
    // the buffer, not starved.
    [Fact]
    public void EvaluateIngotStarvationEmitsNothingWhileBufferHasIngots()
    {
        var planet = CreateColonizedPlanet();
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m); // no refinery → zero production.
        // Buffer seeded at 100 from colonization, NOT emptied → buffer > 0.

        Assert.Empty(planet.EvaluateIngotStarvation(_base));
    }

    // (e) Stale completion: a superseded CompleteBuilding at the original completion time finds a
    // ConstructionHalted slot (status != UnderConstruction) and no-ops.
    [Fact]
    public void StaleCompleteBuildingNoOpsOnConstructionHaltedSlot()
    {
        var planet = CreateColonizedPlanet();
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m);
        var originalCompletesAt = planet.Buildings[0].CompletesAt!.Value;
        EmptyIngotBuffer(planet, _base);
        planet.Apply(Assert.IsType<ConstructionHalted>(Assert.Single(planet.EvaluateIngotStarvation(_base))));
        Assert.Equal(BuildingStatus.ConstructionHalted, planet.Buildings[0].Status);

        Assert.Empty(planet.CompleteBuilding(0, originalCompletesAt));
        Assert.Equal(BuildingStatus.ConstructionHalted, planet.Buildings[0].Status); // unchanged.
    }

    // (f) EvaluateIngotStarvation halts BOTH consumer kinds on the same trigger — an UnderConstruction
    // building AND an Active ship build (#83, Task 2).
    [Fact]
    public void EvaluateIngotStarvationHaltsBothBuildingAndShipBuild()
    {
        var planet = HaltedBuildingAndShipPlanet(out var shipId);

        Assert.Equal(BuildingStatus.ConstructionHalted, planet.Buildings[2].Status);
        Assert.Equal(ShipBuildStatus.Halted, planet.ShipQueue.Single(b => b.Id == shipId).Status);
    }

    // (g) EvaluateIngotStarvationResumes emits a resume for EVERY paused consumer once ingots return —
    // ConstructionResumed per ConstructionHalted building AND ShipBuildResumed per Halted ship build —
    // and [] while still starved (no production AND an empty buffer).
    [Fact]
    public void EvaluateIngotStarvationResumesEmitsBothConsumerKindsWhenIngotsReturn()
    {
        var planet = HaltedBuildingAndShipPlanet(out var shipId);

        // Still starved (empty buffer, zero production) → no resumes.
        Assert.Empty(planet.EvaluateIngotStarvationResumes(_base));

        // Ingots return (buffer refilled) → both consumer kinds resume.
        var resumeAt = _base.AddSeconds(200);
        planet.IronIngot = planet.IronIngot with { CheckpointValue = 500m, CheckpointTime = resumeAt };
        var resumes = planet.EvaluateIngotStarvationResumes(resumeAt);

        Assert.Equal(2, resumes.Count);
        Assert.Equal(2, Assert.Single(resumes.OfType<ConstructionResumed>()).SlotIndex);
        Assert.Equal(shipId, Assert.Single(resumes.OfType<ShipBuildResumed>()).BuildId);
    }

    // A planet driven into ingot starvation with BOTH an UnderConstruction building (slot 2) and an
    // Active ship build halted: Generator + Shipyard operational (so the ship can be active and zero
    // ingots are produced — no refinery), an under-construction Drill, an active ship build, empty buffer.
    private static Planet HaltedBuildingAndShipPlanet(out Guid shipId)
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Shipyard);
        StartBuild(planet, BuildingType.Drill, _base, 100m, 100m); // slot 2, under construction.

        shipId = Guid.NewGuid();
        planet.Apply(new ShipConstructionQueued(shipId, ShipType.ColonyShip, _base, 10m, 30m));
        planet.Apply(new ShipConstructionStarted(shipId, _base, _base.AddSeconds(30)));
        EmptyIngotBuffer(planet, _base);

        foreach (var e in planet.EvaluateIngotStarvation(_base))
        {
            switch (e)
            {
                case ConstructionHalted h: planet.Apply(h); break;
                case ShipBuildHalted h: planet.Apply(h); break;
            }
        }

        return planet;
    }
}
