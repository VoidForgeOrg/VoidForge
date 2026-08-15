namespace Voidforge.Api.Domain.Events;

public sealed record FleetAssembled(Guid OwnerId, Guid PlanetId, IReadOnlyList<FleetShip> Ships, DateTimeOffset AssembledAt);
