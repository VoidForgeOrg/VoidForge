namespace Voidforge.Api.Domain;

// A completed ship. CompletedAt gives the roster a stable, meaningful default sort.
// OwnerId (D13) is the owner of the planet at completion time; assembly validates ship
// ownership rather than planet ownership so ships disbanded onto a foreign or unowned
// planet's roster stay reachable by their owner. Nullable to mirror Planet.OwnerId;
// pre-#48 snapshots deserialize with null (dev worlds reseed).
public sealed record RosterShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt, Guid? OwnerId);
