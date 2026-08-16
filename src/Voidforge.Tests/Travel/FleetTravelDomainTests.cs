using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Travel;
using Xunit;

namespace Voidforge.Tests.Travel;

public sealed class FleetTravelDomainTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static decimal SpeedOf(ShipType type) => type switch
    {
        ShipType.ColonyShip => 0.05m,
        ShipType.CargoVessel => 0.10m,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static (Fleet Fleet, Guid OwnerId, Guid PlanetId) AssembledFleet(params ShipType[] shipTypes)
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var ships = shipTypes
            .Select(type => new RosterShip(Guid.NewGuid(), type, _t0, ownerId))
            .ToList();
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, ships, _t0.AddSeconds(10)));
        return (fleet, ownerId, planetId);
    }

    private static TravelPlan Plan(DateTimeOffset arrivesAt)
        => new(ArrivesAt: arrivesAt, TotalDistance: 7m, Legs: [new TravelLeg(null, 7m, arrivesAt)]);

    [Fact]
    public void GetSpeedPicksTheSlowestShip()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip, ShipType.CargoVessel);

        var speed = fleet.GetSpeed(SpeedOf);

        Assert.Equal(0.05m, speed);
    }

    [Fact]
    public void GetSpeedOnEmptyFleetThrows()
    {
        var fleet = new Fleet();

        Assert.Throws<InvalidOperationException>(() => fleet.GetSpeed(SpeedOf));
    }

    [Fact]
    public void DepartOnStationedFleetProducesCorrectEventAndAppliesToInTransit()
    {
        var (fleet, _, planetId) = AssembledFleet(ShipType.ColonyShip);
        var destinationPlanetId = Guid.NewGuid();
        var departAt = _t0.AddSeconds(20);
        var arrivesAt = departAt.AddSeconds(140);
        var plan = Plan(arrivesAt);

        var @event = fleet.Depart(destinationPlanetId, MissionType.Move, plan, departAt);

        Assert.Equal(planetId, @event.OriginPlanetId);
        Assert.Equal(destinationPlanetId, @event.DestinationPlanetId);
        Assert.Equal(MissionType.Move, @event.Mission);
        Assert.Equal(departAt, @event.DepartedAt);
        Assert.Equal(plan, @event.Plan);

        fleet.Apply(@event);

        Assert.Equal(FleetStatus.InTransit, fleet.Status);
        Assert.Null(fleet.LocationPlanetId);
        Assert.Equal(planetId, fleet.OriginPlanetId);
        Assert.Equal(destinationPlanetId, fleet.DestinationPlanetId);
        Assert.Equal(MissionType.Move, fleet.Mission);
        Assert.Equal(departAt, fleet.DepartedAt);
        Assert.Equal(arrivesAt, fleet.ArrivesAt);
        Assert.Equal(plan, fleet.TravelPlan);
    }

    [Fact]
    public void DepartWhileInTransitThrows()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);
        var departAt = _t0.AddSeconds(20);
        var plan = Plan(departAt.AddSeconds(140));
        fleet.Apply(fleet.Depart(Guid.NewGuid(), MissionType.Move, plan, departAt));

        Assert.Throws<InvalidOperationException>(
            () => fleet.Depart(Guid.NewGuid(), MissionType.Move, plan, departAt.AddSeconds(1)));
    }

    [Fact]
    public void ArriveWithMatchingArrivesAtProducesFleetArrivedAndStationsTheFleet()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);
        var destinationPlanetId = Guid.NewGuid();
        var departAt = _t0.AddSeconds(20);
        var arrivesAt = departAt.AddSeconds(140);
        var plan = Plan(arrivesAt);
        fleet.Apply(fleet.Depart(destinationPlanetId, MissionType.Move, plan, departAt));

        var events = fleet.Arrive(arrivesAt);

        var arrived = Assert.Single(events);
        var fleetArrived = Assert.IsType<FleetArrived>(arrived);
        Assert.Equal(destinationPlanetId, fleetArrived.DestinationPlanetId);
        Assert.Equal(arrivesAt, fleetArrived.ArrivedAt);

        fleet.Apply(fleetArrived);

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(destinationPlanetId, fleet.LocationPlanetId);
        Assert.Null(fleet.OriginPlanetId);
        Assert.Null(fleet.DestinationPlanetId);
        Assert.Null(fleet.Mission);
        Assert.Null(fleet.DepartedAt);
        Assert.Null(fleet.ArrivesAt);
        Assert.Null(fleet.TravelPlan);
    }

    [Fact]
    public void ArriveWithWrongArrivesAtIsAStaleNoOp()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);
        var destinationPlanetId = Guid.NewGuid();
        var departAt = _t0.AddSeconds(20);
        var arrivesAt = departAt.AddSeconds(140);
        var plan = Plan(arrivesAt);
        fleet.Apply(fleet.Depart(destinationPlanetId, MissionType.Move, plan, departAt));

        var events = fleet.Arrive(arrivesAt.AddSeconds(1));

        Assert.Empty(events);
        Assert.Equal(FleetStatus.InTransit, fleet.Status);
    }

    [Fact]
    public void ArriveWhileStationedIsANoOp()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);

        var events = fleet.Arrive(_t0.AddSeconds(999));

        Assert.Empty(events);
        Assert.Equal(FleetStatus.Stationed, fleet.Status);
    }

    [Fact]
    public void RecallProducesReturnPlanHeadingToOriginKeepsInTransit()
    {
        var (fleet, _, originPlanetId) = AssembledFleet(ShipType.ColonyShip, ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(15)));
        var shipIdsBefore = fleet.Ships.Select(s => s.Id).ToList();
        var destinationPlanetId = Guid.NewGuid();
        var departAt = _t0.AddSeconds(20);
        var plan = Plan(departAt.AddSeconds(140));
        fleet.Apply(fleet.Depart(destinationPlanetId, MissionType.Move, plan, departAt));

        var elapsed = TimeSpan.FromSeconds(40);
        var recallAt = departAt + elapsed;
        var @event = fleet.Recall(recallAt);

        Assert.Equal(recallAt, @event.RecalledAt);
        Assert.Equal(recallAt + elapsed, @event.ReturnPlan.ArrivesAt);
        var leg = Assert.Single(@event.ReturnPlan.Legs);
        Assert.Equal(originPlanetId, leg.WaypointPlanetId);
        Assert.Equal(plan.TotalDistance, @event.ReturnPlan.TotalDistance);

        fleet.Apply(@event);

        Assert.Equal(FleetStatus.InTransit, fleet.Status);
        Assert.Equal(originPlanetId, fleet.DestinationPlanetId);
        Assert.Equal(recallAt + elapsed, fleet.ArrivesAt);
        Assert.Equal(recallAt, fleet.DepartedAt);
        Assert.Equal(MissionType.Move, fleet.Mission);
        Assert.Equal(recallAt, fleet.RecalledAt);
        Assert.Equal(@event.ReturnPlan, fleet.TravelPlan);
        // Cargo and ships survive the recall untouched.
        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        Assert.Equal(shipIdsBefore, fleet.Ships.Select(s => s.Id).ToList());
    }

    [Fact]
    public void RecalledArrivalStationsAtOriginWithCargoIntact()
    {
        var (fleet, _, originPlanetId) = AssembledFleet(ShipType.ColonyShip, ShipType.CargoVessel);
        fleet.Apply(new CargoLoaded(100m, 50m, _t0.AddSeconds(15)));
        var shipIdsBefore = fleet.Ships.Select(s => s.Id).ToList();
        var departAt = _t0.AddSeconds(20);
        var plan = Plan(departAt.AddSeconds(140));
        fleet.Apply(fleet.Depart(Guid.NewGuid(), MissionType.Move, plan, departAt));
        var recallAt = departAt + TimeSpan.FromSeconds(40);
        var recalled = fleet.Recall(recallAt);
        fleet.Apply(recalled);
        var returnArrivesAt = recalled.ReturnPlan.ArrivesAt;

        var arrived = Assert.IsType<FleetArrived>(Assert.Single(fleet.Arrive(returnArrivesAt)));
        Assert.Equal(originPlanetId, arrived.DestinationPlanetId);
        fleet.Apply(arrived);

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(originPlanetId, fleet.LocationPlanetId);
        Assert.Null(fleet.RecalledAt);
        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        Assert.Equal(shipIdsBefore, fleet.Ships.Select(s => s.Id).ToList());
    }

    [Fact]
    public void RecallOfStationedFleetThrows()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);

        Assert.Throws<InvalidOperationException>(() => fleet.Recall(_t0.AddSeconds(30)));
    }

    [Fact]
    public void RecallOfAlreadyRecalledFleetThrows()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);
        var departAt = _t0.AddSeconds(20);
        var plan = Plan(departAt.AddSeconds(140));
        fleet.Apply(fleet.Depart(Guid.NewGuid(), MissionType.Move, plan, departAt));
        var recallAt = departAt + TimeSpan.FromSeconds(40);
        fleet.Apply(fleet.Recall(recallAt));

        Assert.Throws<InvalidOperationException>(() => fleet.Recall(recallAt.AddSeconds(5)));
    }

    // A delayed durable arrival can leave the fleet InTransit at/after ArrivesAt; recalling then would
    // build a return trip from beyond the destination (elapsed > outbound duration), so Recall rejects it.
    [Fact]
    public void RecallAtOrAfterArrivalThrows()
    {
        var (fleet, _, _) = AssembledFleet(ShipType.ColonyShip);
        var departAt = _t0.AddSeconds(20);
        var arrivesAt = departAt.AddSeconds(140);
        fleet.Apply(fleet.Depart(Guid.NewGuid(), MissionType.Move, Plan(arrivesAt), departAt));

        Assert.Throws<InvalidOperationException>(() => fleet.Recall(arrivesAt));                 // exactly at arrival
        Assert.Throws<InvalidOperationException>(() => fleet.Recall(arrivesAt.AddSeconds(30)));  // past arrival
        // A recall strictly before arrival still succeeds.
        Assert.NotNull(fleet.Recall(arrivesAt.AddSeconds(-1)));
    }

    [Fact]
    public void DisbandAfterArrivalSucceedsAtTheNewLocation()
    {
        var (fleet, ownerId, _) = AssembledFleet(ShipType.ColonyShip);
        var destinationPlanetId = Guid.NewGuid();
        var departAt = _t0.AddSeconds(20);
        var arrivesAt = departAt.AddSeconds(140);
        var plan = Plan(arrivesAt);
        fleet.Apply(fleet.Depart(destinationPlanetId, MissionType.Move, plan, departAt));
        var arrived = Assert.IsType<FleetArrived>(Assert.Single(fleet.Arrive(arrivesAt)));
        fleet.Apply(arrived);

        var roster = fleet.ToRosterShips();
        fleet.Apply(fleet.Disband(arrivesAt.AddSeconds(5)));

        Assert.Equal(FleetStatus.Disbanded, fleet.Status);
        var returned = Assert.Single(roster);
        Assert.Equal(ownerId, returned.OwnerId);
    }
}
