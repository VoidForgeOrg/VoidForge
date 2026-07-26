using Voidforge.Api.Domain;

namespace Voidforge.Api.Domain.Events;

// Ships return to the roster on disband. Carries full RosterShip records (with OwnerId)
// so the Apply is a plain add and the fleet owner survives the round-trip (D13).
public sealed record ShipsAddedToRoster(Guid FleetId, IReadOnlyList<RosterShip> Ships, DateTimeOffset At);
