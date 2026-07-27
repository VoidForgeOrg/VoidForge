using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Colonize;

public sealed class ColonizeDomainTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fleet AssembledFleet(Guid ownerId, Guid planetId, IReadOnlyList<RosterShip> ships)
    {
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, ships, _t0));
        return fleet;
    }

    // A freshly created, uncolonized planet: empty pools, generous capacity, no buildings —
    // mirrors PlanetStorageMutationTests.BarePlanet.
    private static Planet BarePlanet(long capacity = 1000)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, capacity, capacity, 0m, 0m, 0m));
        return planet;
    }

    [Fact]
    public void ConsumeColonyShipPicksTheOldestColonyShipAndRemovesExactlyIt()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var cargoVessel = new RosterShip(Guid.NewGuid(), ShipType.CargoVessel, _t0, ownerId);
        var newestColonyShip = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0.AddSeconds(30), ownerId);
        var oldestColonyShip = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0.AddSeconds(10), ownerId);
        var middleColonyShip = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0.AddSeconds(20), ownerId);
        var fleet = AssembledFleet(ownerId, planetId, [cargoVessel, newestColonyShip, oldestColonyShip, middleColonyShip]);
        var destinationId = Guid.NewGuid();
        var at = _t0.AddSeconds(100);

        var @event = fleet.ConsumeColonyShip(destinationId, at);

        Assert.Equal(destinationId, @event.PlanetId);
        Assert.Equal(oldestColonyShip.Id, @event.ShipId);
        Assert.Equal(at, @event.ConsumedAt);

        fleet.Apply(@event);

        Assert.Equal(3, fleet.Ships.Count);
        Assert.DoesNotContain(fleet.Ships, s => s.Id == oldestColonyShip.Id);
        Assert.Contains(fleet.Ships, s => s.Id == cargoVessel.Id);
        Assert.Contains(fleet.Ships, s => s.Id == newestColonyShip.Id);
        Assert.Contains(fleet.Ships, s => s.Id == middleColonyShip.Id);
    }

    [Fact]
    public void ConsumeColonyShipTieBreaksByLowestIdWhenCompletedAtMatches()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var lowerId = new RosterShip(Guid.Parse("00000000-0000-0000-0000-000000000001"), ShipType.ColonyShip, _t0, ownerId);
        var higherId = new RosterShip(Guid.Parse("00000000-0000-0000-0000-000000000002"), ShipType.ColonyShip, _t0, ownerId);
        // Registration order deliberately reversed so the pick can't be an accidental "first in list".
        var fleet = AssembledFleet(ownerId, planetId, [higherId, lowerId]);

        var @event = fleet.ConsumeColonyShip(Guid.NewGuid(), _t0.AddSeconds(10));

        Assert.Equal(lowerId.Id, @event.ShipId);
    }

    [Fact]
    public void ConsumeColonyShipWithNoColonyShipAboardThrows()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var cargoVessel = new RosterShip(Guid.NewGuid(), ShipType.CargoVessel, _t0, ownerId);
        var fleet = AssembledFleet(ownerId, planetId, [cargoVessel]);

        Assert.Throws<InvalidOperationException>(() => fleet.ConsumeColonyShip(Guid.NewGuid(), _t0.AddSeconds(10)));
    }

    [Fact]
    public void RecordColonizationFailureAppliesWithoutStateChange()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var ship = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0, ownerId);
        var fleet = AssembledFleet(ownerId, planetId, [ship]);
        var destinationId = Guid.NewGuid();
        var at = _t0.AddSeconds(10);

        var statusBefore = fleet.Status;
        var locationBefore = fleet.LocationPlanetId;
        var shipIdsBefore = fleet.Ships.Select(s => s.Id).ToList();

        var @event = fleet.RecordColonizationFailure(destinationId, at);
        Assert.Equal(destinationId, @event.PlanetId);
        Assert.Equal(at, @event.At);

        fleet.Apply(@event);

        Assert.Equal(statusBefore, fleet.Status);
        Assert.Equal(locationBefore, fleet.LocationPlanetId);
        Assert.Equal(shipIdsBefore, fleet.Ships.Select(s => s.Id).ToList());
    }

    [Fact]
    public void ClaimOnAnUncolonizedPlanetReturnsTheEventAndApplySetsOwnerWithZeroStores()
    {
        var planet = BarePlanet();
        var ownerId = Guid.NewGuid();
        var at = _t0.AddSeconds(5);

        var @event = planet.Claim(ownerId, at);

        Assert.Equal(ownerId, @event.OwnerId);
        Assert.Equal(0, @event.IronOreStored);
        Assert.Equal(0, @event.IronIngotStored);
        Assert.Equal(at, @event.ColonizedAt);

        planet.Apply(@event);

        Assert.Equal(ownerId, planet.OwnerId);
        Assert.Equal(0m, planet.IronOre.CheckpointValue);
        Assert.Equal(0m, planet.IronIngot.CheckpointValue);
        Assert.Equal(at, planet.IronOre.CheckpointTime);
        Assert.Equal(at, planet.IronIngot.CheckpointTime);
    }

    [Fact]
    public void ClaimOnAnAlreadyOwnedPlanetThrows()
    {
        var planet = BarePlanet();
        planet.Apply(planet.Claim(Guid.NewGuid(), _t0));

        Assert.Throws<InvalidOperationException>(() => planet.Claim(Guid.NewGuid(), _t0.AddSeconds(5)));
    }
}
