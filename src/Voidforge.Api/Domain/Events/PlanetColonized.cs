namespace Voidforge.Api.Domain.Events;

public sealed record PlanetColonized(Guid OwnerId, long IronOreStored, long IronIngotStored);
