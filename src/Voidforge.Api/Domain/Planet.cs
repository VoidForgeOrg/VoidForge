using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

public sealed class Planet
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid SolarSystemId { get; set; }
    public Guid? OwnerId { get; set; }
    public long IronOrePool { get; set; }
    public int BuildingSlotCount { get; set; }
    public long IronOreStorageCapacity { get; set; }
    public long IronIngotStorageCapacity { get; set; }
    public long IronOreStored { get; set; }
    public long IronIngotStored { get; set; }

    public void Apply(PlanetCreated @event)
    {
        Name = @event.Name;
        SolarSystemId = @event.SolarSystemId;
        IronOrePool = @event.IronOrePool;
        BuildingSlotCount = @event.BuildingSlotCount;
        IronOreStorageCapacity = @event.IronOreStorageCapacity;
        IronIngotStorageCapacity = @event.IronIngotStorageCapacity;
    }

    public void Apply(PlanetColonized @event)
    {
        OwnerId = @event.OwnerId;
        IronOreStored = @event.IronOreStored;
        IronIngotStored = @event.IronIngotStored;
    }
}
