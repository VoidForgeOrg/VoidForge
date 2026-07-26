namespace Voidforge.Api.Domain.Events;

public sealed record FleetDisbanded(Guid PlanetId, DateTimeOffset DisbandedAt);
