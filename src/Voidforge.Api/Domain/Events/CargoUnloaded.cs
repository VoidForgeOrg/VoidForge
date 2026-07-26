namespace Voidforge.Api.Domain.Events;

public sealed record CargoUnloaded(Guid PlanetId, decimal IronOre, decimal IronIngot, DateTimeOffset UnloadedAt);
