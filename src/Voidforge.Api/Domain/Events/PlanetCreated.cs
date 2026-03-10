namespace Voidforge.Api.Domain.Events;

public sealed record PlanetCreated(
    string Name,
    Guid SolarSystemId,
    long IronOrePool,
    int BuildingSlotCount,
    long IronOreStorageCapacity,
    long IronIngotStorageCapacity);
