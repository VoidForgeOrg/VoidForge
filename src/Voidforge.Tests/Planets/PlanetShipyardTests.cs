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
                case BuildingCompleted b: planet.Apply(b); break;
            }
        }

        return planet;
    }

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
}
