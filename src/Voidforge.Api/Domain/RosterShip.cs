namespace Voidforge.Api.Domain;

// A completed ship. CompletedAt gives the roster a stable, meaningful default sort.
// Fleet assembly (grouping roster ships into fleets) is Phase 4.
public sealed record RosterShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt);
