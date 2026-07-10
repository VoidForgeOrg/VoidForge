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
        RebaseRates(@event.PlacedAt);
    }

    // Pool rates are a pure function of the operational building composition and the
    // energy productivity multiplier m (spec: plans/phase-3-production-chain-design.md
    // §2.2). Checkpoint first so value accrued under the old rates is locked in, then
    // derive the new rates from scratch — incremental deltas would have to un-apply
    // the previous multiplier. Every composition-changing Apply must end with this.
    private void RebaseRates(DateTimeOffset at)
    {
        IronOre = IronOre.Checkpoint(at);
        IronIngot = IronIngot.Checkpoint(at);

        var multiplier = GetProductivityMultiplier();
        var drillExtraction = Buildings
            .Where(b => b.Status == BuildingStatus.Operational)
            .Sum(b => BuildingSpecs.IronOreRatePerSecond(b.Type));

        IronOre = IronOre with { Rate = drillExtraction * multiplier };
        // Refinery conversion lands in #25 (PR 2); until then ingots accrue nothing.
        IronIngot = IronIngot with { Rate = 0m };
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
