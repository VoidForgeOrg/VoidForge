namespace Voidforge.Api.Domain.Events;

public sealed record FleetArrived(Guid DestinationPlanetId, DateTimeOffset ArrivedAt);
