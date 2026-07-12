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

    // Player-initiated placement: validates a free slot and returns the start event carrying
    // the derived drain + completion time. Pure — no mutation until Apply. Balance values are
    // passed in (from the endpoint's IOptions<BalanceOptions>) so replay needs no config.
    public BuildingConstructionStarted StartConstruction(
        BuildingType type, DateTimeOffset now, decimal ingotCost, decimal buildDurationSeconds)
    {
        if (Buildings.Count >= BuildingSlotCount)
        {
            throw new NoFreeSlotsException("No available building slots on this planet.");
        }

        var drain = buildDurationSeconds <= 0 ? 0m : ingotCost / buildDurationSeconds;
        return new BuildingConstructionStarted(
            SlotIndex: Buildings.Count,
            BuildingType: type,
            StartedAt: now,
            CompletesAt: now.AddSeconds((double)buildDurationSeconds),
            DrainPerSecond: drain);
    }

    public void Apply(BuildingConstructionStarted @event)
    {
        Buildings.Add(new BuildingSlot(
            @event.BuildingType,
            BuildingStatus.UnderConstruction,
            @event.CompletesAt,
            @event.DrainPerSecond));
        RebaseRates(@event.StartedAt);
    }

    // Completion is resolved by a durable scheduled message (ADR 0001). Pure + idempotent:
    // returns empty (no-op) unless the slot is still UnderConstruction with a matching
    // CompletesAt — this is the "validate on arrival" guard for stale/superseded messages.
    // Returns a list because a completing Shipyard will also start queued ship builds (#27).
    public IReadOnlyList<object> CompleteBuilding(int slotIndex, DateTimeOffset at)
    {
        if (slotIndex < 0 || slotIndex >= Buildings.Count)
        {
            return [];
        }

        var slot = Buildings[slotIndex];
        if (slot.Status != BuildingStatus.UnderConstruction || slot.CompletesAt != at)
        {
            return [];
        }

        var events = new List<object> { new BuildingCompleted(slotIndex, at) };
        if (slot.Type == BuildingType.Shipyard)
        {
            // This shipyard becomes operational at `at`, raising capacity by ParallelBuilds.
            var newCapacity = BuildingSpecs.ShipyardParallelBuilds * (OperationalShipyardCount() + 1);
            events.AddRange(StartQueuedBuilds(newCapacity - ActiveShipBuildCount(), at));
        }

        return events;
    }

    public void Apply(BuildingCompleted @event)
    {
        var slot = Buildings[@event.SlotIndex];
        Buildings[@event.SlotIndex] = slot with
        {
            Status = BuildingStatus.Operational,
            CompletesAt = null,
            ConstructionDrainPerSecond = 0m,
        };
        RebaseRates(@event.CompletedAt);
    }

    private int OperationalShipyardCount() => Buildings
        .Count(b => b.Status == BuildingStatus.Operational && b.Type == BuildingType.Shipyard);

    private int ActiveShipBuildCount() => ShipQueue.Count(b => b.Status == ShipBuildStatus.Active);

    private int ShipyardCapacity() => BuildingSpecs.ShipyardParallelBuilds * OperationalShipyardCount();

    // Emits ShipConstructionStarted for the first `freeSlots` queued builds (FIFO by QueuedAt),
    // each completing at `at + its stored duration`. Pure — reads current queue state only.
    private List<ShipConstructionStarted> StartQueuedBuilds(int freeSlots, DateTimeOffset at)
    {
        if (freeSlots <= 0)
        {
            return [];
        }

        return ShipQueue
            .Where(b => b.Status == ShipBuildStatus.Queued)
            .OrderBy(b => b.QueuedAt)
            .Take(freeSlots)
            .Select(b => new ShipConstructionStarted(b.Id, at, at.AddSeconds((double)b.BuildDurationSeconds)))
            .ToList();
    }

    // Enqueue is unconditional (D6). If capacity is free the build starts immediately; otherwise
    // it waits. buildId is supplied by the endpoint (Guid.NewGuid) so the method stays pure.
    public IReadOnlyList<object> QueueShip(
        ShipType type, DateTimeOffset now, Guid buildId, decimal drainPerSecond, decimal buildDurationSeconds)
    {
        var events = new List<object>
        {
            new ShipConstructionQueued(buildId, type, now, drainPerSecond, buildDurationSeconds),
        };

        // Invariant: builds never wait while capacity is free, so if there is room the build we
        // just queued is the one that starts.
        if (ActiveShipBuildCount() < ShipyardCapacity())
        {
            events.Add(new ShipConstructionStarted(buildId, now, now.AddSeconds((double)buildDurationSeconds)));
        }

        return events;
    }

    // Durable-message resolution (ADR 0001). Validate-on-arrival: empty (no-op) unless the build
    // is still Active with a matching CompletesAt. On success completes the ship and auto-starts
    // the next queued build (one active slot freed).
    public IReadOnlyList<object> CompleteShipBuild(Guid buildId, DateTimeOffset at)
    {
        var build = ShipQueue.FirstOrDefault(b => b.Id == buildId);
        if (build is null || build.Status != ShipBuildStatus.Active || build.CompletesAt != at)
        {
            return [];
        }

        var events = new List<object> { new ShipCompleted(buildId, at) };
        events.AddRange(StartQueuedBuilds(ShipyardCapacity() - (ActiveShipBuildCount() - 1), at));
        return events;
    }

    // Cancel (D3): no refund. If the cancelled build was Active, a slot frees and the next queued
    // build auto-starts. Unknown build => no-op.
    public IReadOnlyList<object> CancelShipBuild(Guid buildId, DateTimeOffset at)
    {
        var build = ShipQueue.FirstOrDefault(b => b.Id == buildId);
        if (build is null)
        {
            return [];
        }

        var events = new List<object> { new ShipConstructionCancelled(buildId, at) };
        if (build.Status == ShipBuildStatus.Active)
        {
            events.AddRange(StartQueuedBuilds(ShipyardCapacity() - (ActiveShipBuildCount() - 1), at));
        }

        return events;
    }

    public void Apply(ShipConstructionQueued @event)
    {
        ShipQueue.Add(new ShipBuild(
            @event.BuildId, @event.Type, ShipBuildStatus.Queued,
            @event.QueuedAt, StartedAt: null, CompletesAt: null,
            @event.DrainPerSecond, @event.BuildDurationSeconds));
        // Queued builds neither drain nor draw — no rate change until they start.
    }

    public void Apply(ShipConstructionStarted @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        ShipQueue[index] = ShipQueue[index] with
        {
            Status = ShipBuildStatus.Active,
            StartedAt = @event.StartedAt,
            CompletesAt = @event.CompletesAt,
        };
        RebaseRates(@event.StartedAt);   // drain begins; shipyard goes active (energy)
    }

    public void Apply(ShipCompleted @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        var build = ShipQueue[index];
        ShipQueue.RemoveAt(index);
        Ships.Add(new RosterShip(build.Id, build.Type, @event.CompletedAt));
        RebaseRates(@event.CompletedAt);
    }

    public void Apply(ShipConstructionCancelled @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        ShipQueue.RemoveAt(index);
        RebaseRates(@event.CancelledAt);
    }

    private int IndexOfShipBuild(Guid buildId)
    {
        for (var i = 0; i < ShipQueue.Count; i++)
        {
            if (ShipQueue[i].Id == buildId)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Ship build {buildId} not found.");
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

    public decimal GetEnergyConsumptionMw()
    {
        var operational = Buildings.Where(b => b.Status == BuildingStatus.Operational).ToList();
        var nonShipyardDraw = operational
            .Where(b => b.Type != BuildingType.Shipyard)
            .Sum(b => BuildingSpecs.EnergyDrawMw(b.Type));

        var shipyardCount = operational.Count(b => b.Type == BuildingType.Shipyard);
        if (shipyardCount == 0)
        {
            return nonShipyardDraw;
        }

        // Fungible bays: work concentrates into as few shipyards as possible. Those drawing full
        // power = ceil(activeBuilds / ParallelBuilds); the rest idle at 5%.
        var full = BuildingSpecs.EnergyDrawMw(BuildingType.Shipyard);
        var activeShipyards = Math.Min(
            shipyardCount,
            (int)Math.Ceiling(ActiveShipBuildCount() / (double)BuildingSpecs.ShipyardParallelBuilds));
        var shipyardDraw = (activeShipyards * full)
            + ((shipyardCount - activeShipyards) * BuildingSpecs.ShipyardIdleDrawFactor * full);

        return nonShipyardDraw + shipyardDraw;
    }

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
