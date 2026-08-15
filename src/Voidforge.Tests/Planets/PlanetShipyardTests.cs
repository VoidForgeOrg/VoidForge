using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetShipyardTests
{
    // ColonyShip test balance: cost 300, duration 30 => drain 10/s (kept simple for assertions).
    private const decimal _drain = 10m;
    private const decimal _duration = 30m;

    private static Planet PlanetWithShipyards(DateTimeOffset at, int shipyards)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 10, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, at));
        // A generator so shipyards have power, plus the requested operational shipyards.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, at));
        for (var i = 0; i < shipyards; i++)
        {
            planet.Apply(new BuildingPlaced(BuildingType.Shipyard, at));
        }

        return planet;
    }

    private static Planet Apply(Planet planet, IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case ShipConstructionQueued q: planet.Apply(q); break;
                case ShipConstructionStarted s: planet.Apply(s); break;
                case ShipCompleted c: planet.Apply(c); break;
                case ShipConstructionCancelled x: planet.Apply(x); break;
                case ShipBuildHalted h: planet.Apply(h); break;
                case ShipBuildResumed r: planet.Apply(r); break;
                case BuildingCompleted b: planet.Apply(b); break;
            }
        }

        return planet;
    }

    // Pin the stored IronIngot buffer to empty without changing composition — the Rate stays as
    // RebaseRates derived it, which is what the ingot-starvation check runs against (#83).
    private static void EmptyIngotBuffer(Planet planet, DateTimeOffset at) =>
        planet.IronIngot = planet.IronIngot with { CheckpointValue = 0m, CheckpointTime = at };

    [Fact]
    public void QueueShipWithFreeCapacityStartsImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var buildId = Guid.NewGuid();

        var events = planet.QueueShip(ShipType.ColonyShip, now, buildId, _drain, _duration);

        Assert.Equal(2, events.Count);
        Assert.IsType<ShipConstructionQueued>(events[0]);
        var started = Assert.IsType<ShipConstructionStarted>(events[1]);
        Assert.Equal(buildId, started.BuildId);
        Assert.Equal(now.AddSeconds(30), started.CompletesAt);

        Apply(planet, events);
        var build = planet.ShipQueue.Single();
        Assert.Equal(ShipBuildStatus.Active, build.Status);
    }

    [Fact]
    public void QueueShipWithNoShipyardStaysQueued()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 0);

        var events = planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration);

        Assert.Single(events);   // queued only, capacity 0 => no start
        Assert.IsType<ShipConstructionQueued>(events[0]);
        Apply(planet, events);
        Assert.Equal(ShipBuildStatus.Queued, planet.ShipQueue.Single().Status);
    }

    [Fact]
    public void FourthQueuedShipWaitsBehindThreeActive()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);   // capacity 3

        for (var i = 0; i < 3; i++)
        {
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration));
        }

        var fourth = planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration);
        Assert.Single(fourth);   // queued only — 3 already active
        Apply(planet, fourth);

        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active));
        Assert.Equal(1, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Queued));
    }

    [Fact]
    public void CompletingAnActiveBuildAutoStartsNextQueuedFifo()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);   // capacity 3
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now.AddSeconds(i), id, _drain, _duration));
        }

        // Complete the first active build at its scheduled time.
        var first = planet.ShipQueue.First(b => b.Id == ids[0]);
        var completeEvents = planet.CompleteShipBuild(ids[0], first.CompletesAt!.Value);
        Assert.Contains(completeEvents, e => e is ShipCompleted);
        var autoStart = completeEvents.OfType<ShipConstructionStarted>().Single();
        Assert.Equal(ids[3], autoStart.BuildId);   // FIFO: the 4th (only queued) starts

        Apply(planet, completeEvents);
        Assert.Single(planet.Ships);                 // first ship on the roster
        Assert.Equal(ids[0], planet.Ships[0].Id);
        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active));
        Assert.DoesNotContain(planet.ShipQueue, b => b.Status == ShipBuildStatus.Queued);
    }

    [Fact]
    public void CompleteShipBuildIsStaleNoOpOnTimeMismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var id = Guid.NewGuid();
        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, id, _drain, _duration));

        var wrong = planet.CompleteShipBuild(id, now.AddSeconds(999));
        Assert.Empty(wrong);
    }

    [Fact]
    public void CancellingAnActiveBuildAutoStartsNextQueued()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);   // capacity 3
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now.AddSeconds(i), id, _drain, _duration));
        }

        var cancelEvents = planet.CancelShipBuild(ids[0], now.AddSeconds(5));
        Assert.Contains(cancelEvents, e => e is ShipConstructionCancelled);
        var autoStart = cancelEvents.OfType<ShipConstructionStarted>().Single();
        Assert.Equal(ids[3], autoStart.BuildId);

        Apply(planet, cancelEvents);
        Assert.DoesNotContain(planet.ShipQueue, b => b.Id == ids[0]);   // cancelled, no refund
        Assert.Empty(planet.Ships);                                     // cancel != completion
        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active));
    }

    [Fact]
    public void CancellingAQueuedBuildJustRemovesIt()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now.AddSeconds(i), id, _drain, _duration));
        }

        var cancelEvents = planet.CancelShipBuild(ids[3], now.AddSeconds(5));   // the queued one
        Assert.Single(cancelEvents);
        Assert.IsType<ShipConstructionCancelled>(cancelEvents[0]);   // no auto-start (nothing frees)
        Apply(planet, cancelEvents);
        Assert.DoesNotContain(planet.ShipQueue, b => b.Id == ids[3]);
    }

    [Fact]
    public void CancelUnknownBuildIsNoOp()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        Assert.Empty(planet.CancelShipBuild(Guid.NewGuid(), now));
    }

    [Fact]
    public void ActiveBuildDrainsIngots()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var ingotRateBefore = planet.IronIngot.Rate;   // no drills/refineries here => 0

        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration));

        Assert.Equal(ingotRateBefore - _drain, planet.IronIngot.Rate);
    }

    [Fact]
    public void ShipyardDrawsFivePercentWhenIdleAndFullWhenActive()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var full = BuildingSpecs.EnergyDrawMw(BuildingType.Shipyard);

        // Idle shipyard: 5% draw.
        Assert.Equal(full * BuildingSpecs.ShipyardIdleDrawFactor, planet.GetEnergyConsumptionMw());

        // One active build: full draw.
        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration));
        Assert.Equal(full, planet.GetEnergyConsumptionMw());
    }

    [Fact]
    public void BuildingAShipyardAutoStartsAQueuedShip()
    {
        var now = DateTimeOffset.UtcNow;
        var planet = PlanetWithShipyards(now, shipyards: 0);   // capacity 0
        var id = Guid.NewGuid();
        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, id, _drain, _duration));   // queued, waiting

        // Start + complete a Shipyard construction (slot index = current Buildings.Count).
        var started = planet.StartConstruction(BuildingType.Shipyard, now, ingotCost: 60m, buildDurationSeconds: 6m);
        planet.Apply(started);
        var completeEvents = planet.CompleteBuilding(started.SlotIndex, started.CompletesAt);

        var autoStart = completeEvents.OfType<ShipConstructionStarted>().Single();
        Assert.Equal(id, autoStart.BuildId);
        Apply(planet, completeEvents);   // applies BuildingCompleted (via helper) + starts
        planet.Apply((ShipConstructionStarted)autoStart);
        Assert.Equal(ShipBuildStatus.Active, planet.ShipQueue.Single(b => b.Id == id).Status);
    }

    // #83: an active ship build halts on zero-ingot — its ingot drain drops out of the rate, its state
    // is preserved for resume, and (the subtle part) a lone halted build draws only the 5% idle floor,
    // NOT full power (ActiveShipBuildCount excludes it from the fungible-bay energy math).
    [Fact]
    public void HaltingActiveShipBuildStopsIngotDrainAndDrawsNoFullPower()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var id = Guid.NewGuid();
        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, id, _drain, _duration));

        var full = BuildingSpecs.EnergyDrawMw(BuildingType.Shipyard);
        Assert.Equal(-_drain, planet.IronIngot.Rate);          // active build drains ingots.
        Assert.Equal(full, planet.GetEnergyConsumptionMw());   // active build → full shipyard draw.

        EmptyIngotBuffer(planet, now);
        var halt = Assert.IsType<ShipBuildHalted>(Assert.Single(planet.EvaluateIngotStarvation(now)));
        Assert.Equal(id, halt.BuildId);
        planet.Apply(halt);

        var build = planet.ShipQueue.Single();
        Assert.Equal(ShipBuildStatus.Halted, build.Status);
        Assert.Equal(now, build.HaltedAt);
        Assert.Equal(now.AddSeconds((double)_duration), build.CompletesAt);   // preserved for resume.
        Assert.Equal(0m, planet.IronIngot.Rate);   // drain dropped: no production, no active drain.
        // A lone halted build draws the 5% idle floor, NOT full — it must not count as a busy bay.
        Assert.Equal(full * BuildingSpecs.ShipyardIdleDrawFactor, planet.GetEnergyConsumptionMw());
    }

    // #83 bay accounting: a halted ship build keeps its bay occupied, so a newly queued build does NOT
    // auto-start into the same starvation. Capacity 3, all 3 bays held by halted builds → the 4th waits.
    [Fact]
    public void HaltedShipBuildOccupiesBaySoQueuedDoesNotAutoStart()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var planet = PlanetWithShipyards(now, shipyards: 1);   // capacity 3
        for (var i = 0; i < 3; i++)
        {
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, Guid.NewGuid(), _drain, _duration));
        }

        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active));

        // Starve → all 3 active builds halt, occupying all 3 bays.
        EmptyIngotBuffer(planet, now);
        var halts = planet.EvaluateIngotStarvation(now);
        Assert.Equal(3, halts.Count);
        Assert.All(halts, e => Assert.IsType<ShipBuildHalted>(e));
        Apply(planet, halts);
        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Halted));

        // A newly queued build must NOT auto-start — every bay is held by a halted build.
        var fourthId = Guid.NewGuid();
        var fourth = planet.QueueShip(ShipType.ColonyShip, now, fourthId, _drain, _duration);
        Assert.Single(fourth);                                  // queued only, no ShipConstructionStarted.
        Assert.IsType<ShipConstructionQueued>(fourth[0]);
        Apply(planet, fourth);
        Assert.Equal(ShipBuildStatus.Queued, planet.ShipQueue.Single(b => b.Id == fourthId).Status);
    }

    // #83: resume restores the build to Active, pushes CompletesAt out by exactly the paused span
    // (resumeAt + (originalCompletesAt − haltedAt)), clears HaltedAt, and restores the ingot drain.
    [Fact]
    public void ResumingHaltedShipBuildRestoresActiveAndPushesCompletion()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var planet = PlanetWithShipyards(now, shipyards: 1);
        var id = Guid.NewGuid();
        Apply(planet, planet.QueueShip(ShipType.ColonyShip, now, id, _drain, _duration));
        var originalCompletesAt = planet.ShipQueue.Single().CompletesAt!.Value;   // now + 30s.

        EmptyIngotBuffer(planet, now);
        var haltAt = now.AddSeconds(10);   // 20s of work remaining at the pause.
        Apply(planet, planet.EvaluateIngotStarvation(haltAt));
        Assert.Equal(ShipBuildStatus.Halted, planet.ShipQueue.Single().Status);
        Assert.Equal(haltAt, planet.ShipQueue.Single().HaltedAt);
        Assert.Equal(0m, planet.IronIngot.Rate);   // drain dropped while halted.

        var resumeAt = now.AddSeconds(100);
        planet.Apply(new ShipBuildResumed(id, resumeAt));

        var build = planet.ShipQueue.Single();
        Assert.Equal(ShipBuildStatus.Active, build.Status);
        Assert.Null(build.HaltedAt);
        Assert.Equal(resumeAt + (originalCompletesAt - haltAt), build.CompletesAt);
        Assert.Equal(now.AddSeconds(120), build.CompletesAt);   // 100 + 20.
        Assert.Equal(-_drain, planet.IronIngot.Rate);           // drain restored.
    }

    // #83 bay accounting must not regress NORMAL auto-start: with nothing halted, OccupiedBayCount ==
    // ActiveShipBuildCount, so a completing build still auto-starts the next queued build.
    [Fact]
    public void NonStarvedCompletionStillAutoStartsNextQueued()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var planet = PlanetWithShipyards(now, shipyards: 1);   // capacity 3
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            Apply(planet, planet.QueueShip(ShipType.ColonyShip, now.AddSeconds(i), id, _drain, _duration));
        }

        var first = planet.ShipQueue.First(b => b.Id == ids[0]);
        var completeEvents = planet.CompleteShipBuild(ids[0], first.CompletesAt!.Value);
        var autoStart = completeEvents.OfType<ShipConstructionStarted>().Single();
        Assert.Equal(ids[3], autoStart.BuildId);   // the 4th (only queued) starts into the freed bay.

        Apply(planet, completeEvents);
        Assert.Equal(3, planet.ShipQueue.Count(b => b.Status == ShipBuildStatus.Active));
    }
}
