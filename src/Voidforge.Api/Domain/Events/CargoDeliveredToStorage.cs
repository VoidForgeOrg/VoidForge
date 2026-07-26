namespace Voidforge.Api.Domain.Events;

public sealed record CargoDeliveredToStorage(Guid FleetId, decimal IronOre, decimal IronIngot, DateTimeOffset At);
