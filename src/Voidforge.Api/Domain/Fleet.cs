using Voidforge.Api.Domain.Events;
using Voidforge.Api.Travel;

namespace Voidforge.Api.Domain;

// Event-sourced aggregate (spec D1): fleets outlive any single mission. Inline snapshot
// like Player/Planet. Travel state (#49) and cargo (#50) extend this class.
public sealed class Fleet
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public FleetStatus Status { get; set; }
    public Guid? LocationPlanetId { get; set; }
    public DateTimeOffset AssembledAt { get; set; }
    public IList<FleetShip> Ships { get; set; } = [];

    // Transit snapshot (#49): all null while Stationed. Populated by Depart, cleared by
    // Arrive (D6 — arrival always leaves the fleet Stationed; the roster path is disband).
    // Existing pre-#49 snapshots deserialize with these null, which is correct: a fleet
    // that predates travel was necessarily Stationed.
    public Guid? OriginPlanetId { get; set; }
    public Guid? DestinationPlanetId { get; set; }
    public MissionType? Mission { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }
    public DateTimeOffset? ArrivesAt { get; set; }
    public TravelPlan? TravelPlan { get; set; }

    // Cargo snapshot (#50): decimal totals of the two cargo types. Per D8/D9, these are
    // populated by CargoLoaded, decremented by CargoUnloaded. Existing pre-#50 snapshots
    // deserialize with these 0, which is correct: a fleet that predates cargo has no cargo.
    public decimal CargoIronOre { get; set; }
    public decimal CargoIronIngot { get; set; }

    // Pure factory: the endpoint validates ship ownership (D13) and roster membership
    // before calling. Ships map 1:1 so the roster's sort key survives the round-trip.
    public static FleetAssembled Assemble(
        Guid ownerId, Guid planetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at)
        => new(
            ownerId,
            planetId,
            ships.Select(s => new FleetShip(s.Id, s.Type, s.CompletedAt)).ToList(),
            at);

    public void Apply(FleetAssembled @event)
    {
        OwnerId = @event.OwnerId;
        Status = FleetStatus.Stationed;
        LocationPlanetId = @event.PlanetId;
        AssembledAt = @event.AssembledAt;
        Ships = [.. @event.Ships];
    }

    // FleetShip carries no speed of its own — injecting the lookup keeps the aggregate
    // config-free (Phase 3 D10 principle). The launch endpoint passes
    // t => balance.Ships.For(t).SpeedPerSecond. An empty fleet cannot arise via the API
    // (assembly requires at least one ship) so this is a programming-error guard, not a
    // user-facing one.
    public decimal GetSpeed(Func<ShipType, decimal> speedOf)
    {
        if (Ships.Count == 0)
        {
            throw new InvalidOperationException("Cannot compute speed for an empty fleet.");
        }

        return Ships.Min(s => speedOf(s.Type));
    }

    // Per D8: sums cargo capacity across all ships in the fleet. Like GetSpeed,
    // it's config-free by design: the endpoint injects a lookup that maps ship types to
    // their capacities (e.g., t => t == ShipType.CargoVessel ? 500m : 0m).
    public decimal GetCargoCapacity(Func<ShipType, decimal> capacityOf)
        => Ships.Sum(s => capacityOf(s.Type));

    // Per D8: returns the total mass/volume of cargo currently aboard.
    public decimal GetCargoLoad() => CargoIronOre + CargoIronIngot;

    // Stationed-only: the endpoint has already resolved the destination and travel plan.
    // Origin is the fleet's current location — captured before Apply blanks it.
    public FleetDeparted Depart(Guid destinationPlanetId, MissionType mission, TravelPlan plan, DateTimeOffset at)
    {
        if (Status != FleetStatus.Stationed || LocationPlanetId is null)
        {
            throw new InvalidOperationException("Only a stationed fleet can depart.");
        }

        return new FleetDeparted(LocationPlanetId.Value, destinationPlanetId, mission, at, plan);
    }

    public void Apply(FleetDeparted @event)
    {
        Status = FleetStatus.InTransit;
        LocationPlanetId = null;
        OriginPlanetId = @event.OriginPlanetId;
        DestinationPlanetId = @event.DestinationPlanetId;
        Mission = @event.Mission;
        DepartedAt = @event.DepartedAt;
        ArrivesAt = @event.Plan.ArrivesAt;
        TravelPlan = @event.Plan;
    }

    // Per D9: unload cargo from this fleet at the destination planet. The amounts must
    // be non-negative and cannot exceed what's aboard — these are programming errors,
    // not user-facing. The endpoint computes amounts from the fleet's own cargo state.
    public CargoUnloaded UnloadCargo(Guid planetId, decimal ironOre, decimal ironIngot, DateTimeOffset at)
    {
        if (ironOre < 0 || ironIngot < 0)
        {
            throw new InvalidOperationException("Cargo amounts cannot be negative.");
        }

        if (ironOre > CargoIronOre || ironIngot > CargoIronIngot)
        {
            throw new InvalidOperationException("Cannot unload more cargo than is aboard.");
        }

        return new CargoUnloaded(planetId, ironOre, ironIngot, at);
    }

    // Durable-message resolution (ADR 0001, D2 — supersedes architecture.md §4's Saga
    // sketch). Validate-on-arrival: empty (no-op) unless the fleet is still InTransit with
    // a matching ArrivesAt, so stale or duplicate scheduled messages are harmless.
    // Mission dispatch beyond Move (cargo unload, colonization claim) lands in #50/#51 —
    // returning a list from day one means those PRs only add elements, not reshape this.
    // Returns Fleet-stream events only; planet-side arrival effects are produced from the
    // Planet aggregate and appended by the handler onto the planet's own stream.
    public IReadOnlyList<object> Arrive(DateTimeOffset at)
    {
        if (Status != FleetStatus.InTransit || ArrivesAt != at)
        {
            return [];
        }

        return [new FleetArrived(DestinationPlanetId!.Value, at)];
    }

    public void Apply(FleetArrived @event)
    {
        Status = FleetStatus.Stationed;
        LocationPlanetId = @event.DestinationPlanetId;
        // D6: arrival always leaves the fleet stationed; disband is the only path back to
        // the roster. Clear the whole transit block — nothing about the just-finished
        // mission survives on the snapshot.
        OriginPlanetId = null;
        DestinationPlanetId = null;
        Mission = null;
        DepartedAt = null;
        ArrivesAt = null;
        TravelPlan = null;
    }

    // Stationed-only (409 at the endpoint). Per D11, cannot disband a fleet with cargo aboard.
    public FleetDisbanded Disband(DateTimeOffset at)
    {
        if (Status != FleetStatus.Stationed || LocationPlanetId is null)
        {
            throw new InvalidOperationException("Only a stationed fleet can be disbanded.");
        }

        if (GetCargoLoad() > 0)
        {
            throw new InvalidOperationException("Cannot disband a fleet with cargo aboard.");
        }

        return new FleetDisbanded(LocationPlanetId.Value, at);
    }

    public void Apply(FleetDisbanded @event)
    {
        _ = @event;
        Status = FleetStatus.Disbanded;
        // LocationPlanetId is intentionally left as-is: it's the fleet's last-known
        // location, not a claim of current presence. Status is the liveness signal
        // (#49/#50: don't read LocationPlanetId as "currently at this planet").
        Ships = [];
    }

    // Per D8: increments cargo totals when cargo is loaded.
    public void Apply(CargoLoaded @event)
    {
        CargoIronOre += @event.IronOre;
        CargoIronIngot += @event.IronIngot;
    }

    // Per D9: decrements cargo totals when cargo is unloaded.
    public void Apply(CargoUnloaded @event)
    {
        CargoIronOre -= @event.IronOre;
        CargoIronIngot -= @event.IronIngot;
    }

    // Ships leave carrying the fleet owner's id (D13) so they stay assemblable wherever
    // they land — including a foreign or unowned planet's roster.
    public IReadOnlyList<RosterShip> ToRosterShips()
        => Ships.Select(s => new RosterShip(s.Id, s.Type, s.CompletedAt, OwnerId)).ToList();
}
