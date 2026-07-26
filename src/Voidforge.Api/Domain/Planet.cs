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

    // Raw `with` (not the non-regressing Checkpoint) is safe here specifically: a claimable
    // planet's pools are zero-rate/zero-value (nothing has ever accrued, so there is nothing
    // to lose by overwriting CheckpointTime outright), and homeworld seeding passes its
    // starting stores explicitly via this same event rather than deriving them from an
    // in-flight accrual. Fleet colonization always claims through Claim (zero stores, §2.4);
    // registration's richer seeded colonization is the other caller of this event/Apply.
    public void Apply(PlanetColonized @event)
    {
        OwnerId = @event.OwnerId;
        IronOre = IronOre with { CheckpointValue = @event.IronOreStored, CheckpointTime = @event.ColonizedAt };
        IronIngot = IronIngot with { CheckpointValue = @event.IronIngotStored, CheckpointTime = @event.ColonizedAt };
    }

    // The D10 null-owner assertion (spec §2.4): guards the claim itself. A genuine race
    // between two fleets (or a fleet and registration) is resolved one level up by
    // FetchForWriting + ConcurrencyException on the loser's commit, which the #39 retry
    // policy replays whole — the retry re-reads a now-owned planet and lands here again,
    // this time throwing. Zero starting stores (spec §2.4): a fleet-colonized planet starts
    // bare; registration's homeworld seeding uses PlanetColonized directly with its own
    // starting stores, not this factory.
    public PlanetColonized Claim(Guid ownerId, DateTimeOffset at)
    {
        if (OwnerId is not null)
        {
            throw new InvalidOperationException("Planet is already colonized.");
        }

        return new PlanetColonized(ownerId, 0, 0, at);
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

    // Cargo storage mutations (spec §2.5, #50). A fleet loading from or delivering to a
    // planet's buffer is a programming error, not a user-facing one — the endpoint
    // pre-validates for its own 409 response; this is the defensive backstop.
    public CargoLoadedFromStorage LoadCargoFromStorage(
        Guid fleetId, decimal ironOre, decimal ironIngot, DateTimeOffset at)
    {
        if (ironOre < 0 || ironIngot < 0)
        {
            throw new InvalidOperationException("Cargo amounts cannot be negative.");
        }

        if (ironOre > IronOre.GetCurrentValue(at) || ironIngot > IronIngot.GetCurrentValue(at))
        {
            throw new InvalidOperationException("Cannot load more cargo than is in storage.");
        }

        return new CargoLoadedFromStorage(fleetId, ironOre, ironIngot, at);
    }

    // Both cargo Apply methods checkpoint the affected pool at `at` first — non-regressing
    // per #44, so a backwards `at` freezes CheckpointTime but still locks in the value
    // accrued up to it — then adjust only CheckpointValue, clamped to [0, StorageCapacity].
    // Rate is deliberately left untouched and RebaseRates is deliberately NOT called: cargo
    // moves stored value between a ship's hold and the planet's buffer, it never changes
    // which buildings are operational, so it must not perturb the production/consumption
    // rates RebaseRates derives from building composition (spec §2.5 — the only two
    // composition-preserving Apply methods on this aggregate).
    public void Apply(CargoLoadedFromStorage @event)
    {
        IronOre = IronOre.Checkpoint(@event.At);
        IronOre = IronOre with
        {
            CheckpointValue = Math.Clamp(IronOre.CheckpointValue - @event.IronOre, 0, IronOre.StorageCapacity),
        };

        IronIngot = IronIngot.Checkpoint(@event.At);
        IronIngot = IronIngot with
        {
            CheckpointValue = Math.Clamp(IronIngot.CheckpointValue - @event.IronIngot, 0, IronIngot.StorageCapacity),
        };
    }

    // Computes the accepted amounts here (not at the caller) so the event itself carries
    // the truth of what the planet actually took in — callers (the unload endpoint, the
    // Transport arrival handler) read them straight off the event to build the matching
    // Fleet-side CargoUnloaded, rather than re-deriving headroom themselves.
    public CargoDeliveredToStorage AcceptCargoDelivery(
        Guid fleetId, decimal ironOre, decimal ironIngot, DateTimeOffset at)
    {
        if (ironOre < 0 || ironIngot < 0)
        {
            throw new InvalidOperationException("Cargo amounts cannot be negative.");
        }

        var acceptedOre = Math.Min(ironOre, Math.Max(0, IronOre.StorageCapacity - IronOre.GetCurrentValue(at)));
        var acceptedIngot = Math.Min(ironIngot, Math.Max(0, IronIngot.StorageCapacity - IronIngot.GetCurrentValue(at)));

        return new CargoDeliveredToStorage(fleetId, acceptedOre, acceptedIngot, at);
    }

    // See the rationale comment on Apply(CargoLoadedFromStorage): checkpoint-then-clamp,
    // Rate untouched, no RebaseRates.
    public void Apply(CargoDeliveredToStorage @event)
    {
        IronOre = IronOre.Checkpoint(@event.At);
        IronOre = IronOre with
        {
            CheckpointValue = Math.Clamp(IronOre.CheckpointValue + @event.IronOre, 0, IronOre.StorageCapacity),
        };

        IronIngot = IronIngot.Checkpoint(@event.At);
        IronIngot = IronIngot with
        {
            CheckpointValue = Math.Clamp(IronIngot.CheckpointValue + @event.IronIngot, 0, IronIngot.StorageCapacity),
        };
    }
}
