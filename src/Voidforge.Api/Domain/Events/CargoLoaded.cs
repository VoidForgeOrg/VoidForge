namespace Voidforge.Api.Domain.Events;

public sealed record CargoLoaded(decimal IronOre, decimal IronIngot, DateTimeOffset LoadedAt);
