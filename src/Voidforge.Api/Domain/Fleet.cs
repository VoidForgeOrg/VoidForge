using Voidforge.Api.Domain.Events;

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

    // Stationed-only (409 at the endpoint). The cargo-empty guard (D11) arrives with #50.
    public FleetDisbanded Disband(DateTimeOffset at)
    {
        if (Status != FleetStatus.Stationed || LocationPlanetId is null)
        {
            throw new InvalidOperationException("Only a stationed fleet can be disbanded.");
        }

        return new FleetDisbanded(LocationPlanetId.Value, at);
    }

    public void Apply(FleetDisbanded @event)
    {
        _ = @event;
        Status = FleetStatus.Disbanded;
        Ships = [];
    }

    // Ships leave carrying the fleet owner's id (D13) so they stay assemblable wherever
    // they land — including a foreign or unowned planet's roster.
    public IReadOnlyList<RosterShip> ToRosterShips()
        => Ships.Select(s => new RosterShip(s.Id, s.Type, s.CompletedAt, OwnerId)).ToList();
}
