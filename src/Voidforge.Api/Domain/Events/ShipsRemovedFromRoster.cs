namespace Voidforge.Api.Domain.Events;

// Ships leave the roster into a fleet at assembly. Roster mutations do not touch
// resource rates — no RebaseRates in the Apply.
public sealed record ShipsRemovedFromRoster(Guid FleetId, IReadOnlyList<Guid> ShipIds, DateTimeOffset At);
