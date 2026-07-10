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
    public ResourcePool IronOre { get; set; } = new(0, 0, 0, default);
    public ResourcePool IronIngot { get; set; } = new(0, 0, 0, default);
    public IList<BuildingSlot> Buildings { get; set; } = [];

    public void Apply(PlanetCreated @event)
    {
        Name = @event.Name;
        SolarSystemId = @event.SolarSystemId;
        IronOrePool = @event.IronOrePool;
        BuildingSlotCount = @event.BuildingSlotCount;
        IronOre = new ResourcePool(0, 0, @event.IronOreStorageCapacity, default);
        IronIngot = new ResourcePool(0, 0, @event.IronIngotStorageCapacity, default);
    }

    public void Apply(PlanetColonized @event)
    {
        OwnerId = @event.OwnerId;
        IronOre = IronOre with { CheckpointValue = @event.IronOreStored, CheckpointTime = @event.ColonizedAt };
        IronIngot = IronIngot with { CheckpointValue = @event.IronIngotStored, CheckpointTime = @event.ColonizedAt };
    }

    // Validates the slot invariant against current state and produces the event to append.
    // Does not mutate — the resulting BuildingPlaced is applied via Apply once persisted.
    // Ownership/authorization is an application concern and stays at the endpoint.
    public BuildingPlaced PlaceBuilding(BuildingType type, DateTimeOffset placedAt)
    {
        if (Buildings.Count >= BuildingSlotCount)
        {
            throw new NoFreeSlotsException("No available building slots on this planet.");
        }

        return new BuildingPlaced(type, placedAt);
    }

    public void Apply(BuildingPlaced @event)
    {
        Buildings.Add(new BuildingSlot(@event.BuildingType, BuildingStatus.Operational));

        // A building's Iron Ore extraction rate (the Drill's, in Phase 2) is intrinsic to its
        // type — looked up from BuildingSpecs rather than carried on the event. Checkpoint first
        // so ore accrued under the previous rate is locked in before the rate changes; multiple
        // drills are therefore additive.
        var extractionRate = BuildingSpecs.IronOreRatePerSecond(@event.BuildingType);
        if (extractionRate != 0)
        {
            IronOre = IronOre.Checkpoint(@event.PlacedAt) with
            {
                Rate = IronOre.Rate + extractionRate,
            };
        }
    }

    public void CheckpointAllResources(DateTimeOffset now)
    {
        IronOre = IronOre.Checkpoint(now);
        IronIngot = IronIngot.Checkpoint(now);
    }

    // Energy is a flow resource: derived on demand from the operational building
    // composition, never stored (game-design/resources.md). Methods rather than
    // computed properties so they stay out of the Marten snapshot document.
    public decimal GetEnergyGenerationMw() => Buildings
        .Where(b => b.Status == BuildingStatus.Operational)
        .Sum(b => BuildingSpecs.EnergyOutputMw(b.Type));

    public decimal GetEnergyConsumptionMw() => Buildings
        .Where(b => b.Status == BuildingStatus.Operational)
        .Sum(b => BuildingSpecs.EnergyDrawMw(b.Type));

    // In [0, 1]: 1 when demand is met (or there is no demand), generation/consumption
    // when overloaded, 0 when consumers exist but no generator does.
    public decimal GetProductivityMultiplier()
    {
        var consumption = GetEnergyConsumptionMw();
        if (consumption == 0)
        {
            return 1m;
        }

        return Math.Min(1m, GetEnergyGenerationMw() / consumption);
    }
}
