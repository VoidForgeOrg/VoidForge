using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Split by concern into partial files (#40): Planet.cs (state + rate engine),
// Planet.Energy.cs, Planet.Buildings.cs, Planet.Ships.cs. Marten still sees one
// aggregate type, so Apply discovery and the inline snapshot are unaffected.
public sealed partial class Planet
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid SolarSystemId { get; set; }
    public Guid? OwnerId { get; set; }
    public long IronOrePool { get; set; }
    public int BuildingSlotCount { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Z { get; set; }
    public ResourcePool IronOre { get; set; } = new(0, 0, 0, default);
    public ResourcePool IronIngot { get; set; } = new(0, 0, 0, default);
    public IList<BuildingSlot> Buildings { get; set; } = [];
    public IList<ShipBuild> ShipQueue { get; set; } = [];
    public IList<RosterShip> Ships { get; set; } = [];

    public void Apply(PlanetCreated @event)
    {
        Name = @event.Name;
        SolarSystemId = @event.SolarSystemId;
        IronOrePool = @event.IronOrePool;
        BuildingSlotCount = @event.BuildingSlotCount;
        IronOre = new ResourcePool(0, 0, @event.IronOreStorageCapacity, default);
        IronIngot = new ResourcePool(0, 0, @event.IronIngotStorageCapacity, default);
        X = @event.X;
        Y = @event.Y;
        Z = @event.Z;
    }

    // Method (not a property) so it stays out of the Marten snapshot document,
    // same rationale as the energy getters.
    public Coordinates GetCoordinates() => new(X, Y, Z);

    public void Apply(PlanetColonized @event)
    {
        OwnerId = @event.OwnerId;
        IronOre = IronOre with { CheckpointValue = @event.IronOreStored, CheckpointTime = @event.ColonizedAt };
        IronIngot = IronIngot with { CheckpointValue = @event.IronIngotStored, CheckpointTime = @event.ColonizedAt };
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
        var operational = Buildings.Where(b => b.Status == BuildingStatus.Operational).ToList();

        // Drill output and refinery input are both energy-throttled flows.
        var oreInflow = operational.Sum(b => BuildingSpecs.IronOreRatePerSecond(b.Type)) * multiplier;
        var refineryDemand = operational.Sum(b => BuildingSpecs.RefineryOreConsumptionPerSecond(b.Type)) * multiplier;

        // Refineries convert the inflow, not the stored buffer: consumption is clamped to
        // what the drills currently produce, so the net ore rate never goes negative in
        // Phase 3 (buffer-draining + depletion cascades are Phase 5). Even-split falls out
        // for free because the pools are planet-level scalars.
        var effectiveConsumption = Math.Min(refineryDemand, oreInflow);

        var constructionDrain = Buildings
            .Where(b => b.Status == BuildingStatus.UnderConstruction)
            .Sum(b => b.ConstructionDrainPerSecond);

        var shipBuildDrain = ShipQueue
            .Where(b => b.Status == ShipBuildStatus.Active)
            .Sum(b => b.DrainPerSecond);

        IronOre = IronOre with { Rate = oreInflow - effectiveConsumption };
        // Construction (buildings + active ship builds) drains the ingot buffer (NOT scaled by
        // m). The rate may go negative; GetCurrentValue clamps the stored value at 0
        // (zero-ingot halting is Phase 5).
        IronIngot = IronIngot with
        {
            Rate = (BuildingSpecs.RefineryIngotOutputFactor * effectiveConsumption) - constructionDrain - shipBuildDrain,
        };
    }

    public void CheckpointAllResources(DateTimeOffset now)
    {
        IronOre = IronOre.Checkpoint(now);
        IronIngot = IronIngot.Checkpoint(now);
    }
}
