using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Building-lifecycle concern of the Planet aggregate (#40 split).
public sealed partial class Planet
{
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
}
