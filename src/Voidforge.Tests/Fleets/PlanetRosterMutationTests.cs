using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Fleets;

public sealed class PlanetRosterMutationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Builds an owned planet with one completed CargoVessel on the roster, via real events.
    private static (Planet Planet, Guid OwnerId, Guid ShipId) PlanetWithRosterShip()
    {
        var ownerId = Guid.NewGuid();
        var shipId = Guid.NewGuid();
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, 1000, 1000));
        planet.Apply(new PlanetColonized(ownerId, 0, 0, T0));
        planet.Apply(new ShipConstructionQueued(shipId, ShipType.CargoVessel, T0, 0m, 60m));
        planet.Apply(new ShipConstructionStarted(shipId, T0, T0.AddSeconds(60)));
        planet.Apply(new ShipCompleted(shipId, T0.AddSeconds(60)));
        return (planet, ownerId, shipId);
    }

    [Fact]
    public void CompletedShipCarriesThePlanetsOwner()
    {
        var (planet, ownerId, shipId) = PlanetWithRosterShip();

        var ship = Assert.Single(planet.Ships);
        Assert.Equal(shipId, ship.Id);
        Assert.Equal(ownerId, ship.OwnerId);
    }

    [Fact]
    public void RemoveShipsFromRosterRemovesExactlyThoseShips()
    {
        var (planet, _, shipId) = PlanetWithRosterShip();
        var fleetId = Guid.NewGuid();

        var @event = planet.RemoveShipsFromRoster(fleetId, [shipId], T0.AddSeconds(120));
        planet.Apply(@event);

        Assert.Empty(planet.Ships);
        Assert.Equal(fleetId, @event.FleetId);
        Assert.Equal([shipId], @event.ShipIds);
    }

    [Fact]
    public void RemoveShipsFromRosterWithUnknownIdThrows()
    {
        var (planet, _, _) = PlanetWithRosterShip();

        Assert.Throws<InvalidOperationException>(
            () => planet.RemoveShipsFromRoster(Guid.NewGuid(), [Guid.NewGuid()], T0));
    }

    [Fact]
    public void ReturnShipsToRosterRestoresShipsWithOwner()
    {
        var (planet, ownerId, shipId) = PlanetWithRosterShip();
        planet.Apply(planet.RemoveShipsFromRoster(Guid.NewGuid(), [shipId], T0.AddSeconds(120)));

        var returned = new RosterShip(shipId, ShipType.CargoVessel, T0.AddSeconds(60), ownerId);
        planet.Apply(planet.ReturnShipsToRoster(Guid.NewGuid(), [returned], T0.AddSeconds(200)));

        var ship = Assert.Single(planet.Ships);
        Assert.Equal(ownerId, ship.OwnerId);
        Assert.Equal(shipId, ship.Id);
    }
}
