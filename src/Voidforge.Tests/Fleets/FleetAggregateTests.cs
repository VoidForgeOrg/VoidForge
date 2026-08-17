using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Fleets;

[Trait("Category", "Unit")]
public sealed class FleetAggregateTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (Fleet Fleet, Guid OwnerId, Guid PlanetId, RosterShip Ship) AssembledFleet()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var ship = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0, ownerId);
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, [ship], _t0.AddSeconds(10)));
        return (fleet, ownerId, planetId, ship);
    }

    [Fact]
    public void AssembleProducesAStationedFleetAtThePlanet()
    {
        var (fleet, ownerId, planetId, ship) = AssembledFleet();

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(planetId, fleet.LocationPlanetId);
        Assert.Equal(ownerId, fleet.OwnerId);
        var fleetShip = Assert.Single(fleet.Ships);
        Assert.Equal(ship.Id, fleetShip.Id);
        Assert.Equal(ship.CompletedAt, fleetShip.CompletedAt);   // roster sort key survives (§2.1)
    }

    [Fact]
    public void DisbandReturnsShipsCarryingTheFleetOwner()
    {
        var (fleet, ownerId, _, ship) = AssembledFleet();

        var roster = fleet.ToRosterShips();
        fleet.Apply(fleet.Disband(_t0.AddSeconds(20)));

        Assert.Equal(FleetStatus.Disbanded, fleet.Status);
        Assert.Empty(fleet.Ships);
        var returned = Assert.Single(roster);
        Assert.Equal(ownerId, returned.OwnerId);
        Assert.Equal(ship.Id, returned.Id);
    }

    [Fact]
    public void DisbandTwiceThrows()
    {
        var (fleet, _, _, _) = AssembledFleet();
        fleet.Apply(fleet.Disband(_t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(() => fleet.Disband(_t0.AddSeconds(30)));
    }
}
