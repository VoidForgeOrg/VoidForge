using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Building-lifecycle concern of the Planet aggregate (#40 split).
public sealed partial class Planet
{
    // Slots actually occupying capacity (#72): every non-tombstone building. Cancelled/Demolished
    // are terminal tombstones that keep their list position (so SlotIndex stays a stable monotonic
    // id) but free the slot; Demolishing is mid-teardown and still occupies one. The free-slot
    // invariant counts these, not the raw Buildings.Count (which never shrinks — it is append-only).
    private int LiveBuildingCount() => Buildings
        .Count(b => b.Status is not (BuildingStatus.Cancelled or BuildingStatus.Demolished));

    // Validates the slot invariant against current state and produces the event to append.
    // Does not mutate — the resulting BuildingPlaced is applied via Apply once persisted.
    // Ownership/authorization is an application concern and stays at the endpoint.
    public BuildingPlaced PlaceBuilding(BuildingType type, DateTimeOffset placedAt)
    {
        if (LiveBuildingCount() >= BuildingSlotCount)
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

    // Player-initiated placement: validates a free slot and returns the start event carrying
    // the derived drain + completion time. Pure — no mutation until Apply. Balance values are
    // passed in (from the endpoint's IOptions<BalanceOptions>) so replay needs no config.
    public BuildingConstructionStarted StartConstruction(
        BuildingType type, DateTimeOffset now, decimal ingotCost, decimal buildDurationSeconds)
    {
        if (LiveBuildingCount() >= BuildingSlotCount)
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

    // Cancel in-progress construction (#72): no refund. Pure + validate-here — returns empty
    // (no-op) unless the slot exists and is still UnderConstruction. Cancelling anything else
    // (Operational, a tombstone, or a demolition) is rejected at the endpoint as a 409, and this
    // is the defensive backstop. The slot becomes a Cancelled tombstone via Apply, never removed —
    // so an in-flight CompleteBuildingConstruction message finds the tombstone and no-ops.
    public IReadOnlyList<object> CancelConstruction(int slotIndex, DateTimeOffset at)
    {
        if (slotIndex < 0 || slotIndex >= Buildings.Count)
        {
            return [];
        }

        if (Buildings[slotIndex].Status != BuildingStatus.UnderConstruction)
        {
            return [];
        }

        return [new BuildingConstructionCancelled(slotIndex, at)];
    }

    public void Apply(BuildingConstructionCancelled @event)
    {
        var slot = Buildings[@event.SlotIndex];
        Buildings[@event.SlotIndex] = slot with
        {
            Status = BuildingStatus.Cancelled,
            CompletesAt = null,
            ConstructionDrainPerSecond = 0m,   // drops out of RebaseRates → ingot rate rises
        };
        RebaseRates(@event.At);
    }
}
