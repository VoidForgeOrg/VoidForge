namespace Voidforge.Api.Domain.Events;

public sealed record CompleteShipConstruction(Guid PlanetId, Guid BuildId, DateTimeOffset CompletesAt);
