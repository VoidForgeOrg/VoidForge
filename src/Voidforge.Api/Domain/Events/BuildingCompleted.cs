namespace Voidforge.Api.Domain.Events;

public sealed record BuildingCompleted(int SlotIndex, DateTimeOffset CompletedAt);
