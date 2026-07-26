namespace Voidforge.Api.Domain.Events;

public sealed record CargoLoadedFromStorage(Guid FleetId, decimal IronOre, decimal IronIngot, DateTimeOffset At);
