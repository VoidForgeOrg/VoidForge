namespace Voidforge.Api.Domain;

// Mirrors RosterShip (minus OwnerId — the fleet has one owner) so ships round-trip
// through a fleet without losing the roster's stable sort key (spec §2.1).
public sealed record FleetShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt);
