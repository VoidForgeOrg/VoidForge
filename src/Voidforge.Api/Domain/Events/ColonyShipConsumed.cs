namespace Voidforge.Api.Domain.Events;

public sealed record ColonyShipConsumed(Guid PlanetId, Guid ShipId, DateTimeOffset ConsumedAt);
