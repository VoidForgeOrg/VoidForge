using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Cargo;

[Trait("Category", "Unit")]
public sealed class FleetCargoDomainTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (Fleet Fleet, Guid OwnerId, Guid PlanetId) AssembledFleetWithCargo(ShipType shipType)
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var ship = new RosterShip(Guid.NewGuid(), shipType, _t0, ownerId);
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, [ship], _t0.AddSeconds(10)));
        return (fleet, ownerId, planetId);
    }

    [Fact]
    public void CargoLoadedIncrementsBothTotals()
    {
        var (fleet, _, _) = AssembledFleetWithCargo(ShipType.CargoVessel);

        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
    }

    [Fact]
    public void CargoLoadedCanBeCalledMultipleTimes()
    {
        var (fleet, _, _) = AssembledFleetWithCargo(ShipType.CargoVessel);

        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));
        fleet.Apply(new CargoLoaded(50m, 25m, _t0.AddSeconds(30)));

        Assert.Equal(150m, fleet.CargoIronOre);
        Assert.Equal(75m, fleet.CargoIronIngot);
    }

    [Fact]
    public void CargoUnloadedDecrementsPartially()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        fleet.Apply(new CargoUnloaded(planetId, 40m, 20m, _t0.AddSeconds(30)));

        Assert.Equal(60m, fleet.CargoIronOre);
        Assert.Equal(30m, fleet.CargoIronIngot);
    }

    [Fact]
    public void UnloadCargoThrowsIfIronOreAmountIsNegative()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(
            () => fleet.UnloadCargo(planetId, -1m, 0m, _t0.AddSeconds(30)));
    }

    [Fact]
    public void UnloadCargoThrowsIfIronIngotAmountIsNegative()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(
            () => fleet.UnloadCargo(planetId, 0m, -1m, _t0.AddSeconds(30)));
    }

    [Fact]
    public void UnloadCargoThrowsIfIronOreExceedsAboard()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(
            () => fleet.UnloadCargo(planetId, 101m, 0m, _t0.AddSeconds(30)));
    }

    [Fact]
    public void UnloadCargoThrowsIfIronIngotExceedsAboard()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(
            () => fleet.UnloadCargo(planetId, 0m, 51m, _t0.AddSeconds(30)));
    }

    [Fact]
    public void GetCargoCapacitySumsShipCapacitiesViaLookup()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var cargoShip = new RosterShip(Guid.NewGuid(), ShipType.CargoVessel, _t0, ownerId);
        var colonyShip = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, _t0, ownerId);
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, [cargoShip, colonyShip], _t0.AddSeconds(10)));

        var capacity = fleet.GetCargoCapacity(t => t == ShipType.CargoVessel ? 500m : 0m);

        Assert.Equal(500m, capacity);
    }

    [Fact]
    public void GetCargoLoadReturnsSum()
    {
        var (fleet, _, _) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Equal(150m, fleet.GetCargoLoad());
    }

    [Fact]
    public void DisbandThrowsIfCargoAboard()
    {
        var (fleet, _, _) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(() => fleet.Disband(_t0.AddSeconds(30)));
    }

    [Fact]
    public void DisbandSucceedsAfterFullUnload()
    {
        var (fleet, _, planetId) = AssembledFleetWithCargo(ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(20)));
        fleet.Apply(fleet.UnloadCargo(planetId, 100m, 50m, _t0.AddSeconds(30)));

        var disbandEvent = fleet.Disband(_t0.AddSeconds(40));
        fleet.Apply(disbandEvent);

        Assert.Equal(FleetStatus.Disbanded, fleet.Status);
    }

    [Fact]
    public void DisbandSucceedsWithNoCargo()
    {
        var (fleet, _, _) = AssembledFleetWithCargo(ShipType.CargoVessel);

        var disbandEvent = fleet.Disband(_t0.AddSeconds(30));

        Assert.NotNull(disbandEvent);
        Assert.Equal(FleetStatus.Stationed, fleet.Status);
    }
}
