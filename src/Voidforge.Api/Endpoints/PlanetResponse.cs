namespace Voidforge.Api.Endpoints;

public sealed record PlanetResponse(
    Guid Id,
    string Name,
    Guid SolarSystemId,
    Guid? OwnerId,
    long IronOrePool,
    int BuildingSlotCount,
    long IronOreStorageCapacity,
    long IronIngotStorageCapacity,
    long IronOreStored,
    long IronIngotStored);
