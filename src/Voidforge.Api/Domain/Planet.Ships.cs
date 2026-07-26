using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Ship-queue and roster concern of the Planet aggregate (#40 split).
public sealed partial class Planet
{
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
}
