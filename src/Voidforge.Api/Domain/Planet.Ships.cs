using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Ship-queue and roster concern of the Planet aggregate (#40 split).
public sealed partial class Planet
{
    private int OperationalShipyardCount() => Buildings
        .Count(b => b.Status == BuildingStatus.Operational && b.Type == BuildingType.Shipyard);

    // Active builds only — the fungible-bay ENERGY math (Planet.Energy.cs) counts a halted build as
    // drawing no full power, so this must exclude Halted (#83).
    private int ActiveShipBuildCount() => ShipQueue.Count(b => b.Status == ShipBuildStatus.Active);

    // Bays occupied for AUTO-START capacity purposes (#83): Active AND Halted builds both hold their
    // shipyard bay, so a queued build must not auto-start into a bay a starved (Halted) build still
    // occupies. Distinct from ActiveShipBuildCount (energy). Equal to it whenever nothing is halted, so
    // normal (non-starved) auto-start is unchanged.
    private int OccupiedBayCount() =>
        ShipQueue.Count(b => b.Status is ShipBuildStatus.Active or ShipBuildStatus.Halted);

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
        // just queued is the one that starts. Uses OccupiedBayCount (Active + Halted) so a build does
        // NOT auto-start into a bay a starved (Halted) build still holds (#83).
        if (OccupiedBayCount() < ShipyardCapacity())
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
        // The completing build is Active, so it is counted in OccupiedBayCount; -1 frees its bay. A
        // Halted build still holds its bay, so a queued build only starts into genuine free capacity (#83).
        events.AddRange(StartQueuedBuilds(ShipyardCapacity() - (OccupiedBayCount() - 1), at));
        return events;
    }

    // Cancel (D3): no refund. If the cancelled build held a bay, a slot frees and the next queued build
    // auto-starts. Unknown build => no-op.
    public IReadOnlyList<object> CancelShipBuild(Guid buildId, DateTimeOffset at)
    {
        var build = ShipQueue.FirstOrDefault(b => b.Id == buildId);
        if (build is null)
        {
            return [];
        }

        var events = new List<object> { new ShipConstructionCancelled(buildId, at) };
        // Both Active AND Halted builds occupy a bay (OccupiedBayCount counts both, #83), so cancelling
        // EITHER frees a bay and lets a queued build auto-start; -1 credits the just-freed bay. Cancelling
        // a Halted build previously skipped this, stranding a queued build until an unrelated capacity
        // event. A Queued build holds no bay, so cancelling it frees nothing (guard stays false).
        if (build.Status is ShipBuildStatus.Active or ShipBuildStatus.Halted)
        {
            events.AddRange(StartQueuedBuilds(ShipyardCapacity() - (OccupiedBayCount() - 1), at));
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
        Ships.Add(new RosterShip(build.Id, build.Type, @event.CompletedAt, OwnerId));
        RebaseRates(@event.CompletedAt);
    }

    public void Apply(ShipConstructionCancelled @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        ShipQueue.RemoveAt(index);
        RebaseRates(@event.CancelledAt);
    }

    // Apply(ShipBuildHalted) (#83): pause an active ship build. Status → Halted (leaves the Active set, so
    // RebaseRates' shipBuildDrain filter drops its drain to 0 and the fungible-bay energy math stops
    // counting it as full-power) and stamp HaltedAt. CompletesAt and DrainPerSecond are KEPT: resume
    // needs CompletesAt − HaltedAt for the remaining work, and the kept drain is restored on resume (it
    // is excluded from the drain sum while Halted, so keeping it is harmless). The bay stays occupied for
    // auto-start (OccupiedBayCount counts Halted).
    public void Apply(ShipBuildHalted @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        ShipQueue[index] = ShipQueue[index] with
        {
            Status = ShipBuildStatus.Halted,
            HaltedAt = @event.At,
        };
        RebaseRates(@event.At);
    }

    // Apply(ShipBuildResumed) (#83): resume a paused ship build. The remaining work captured at halt was
    // CompletesAt − HaltedAt (both non-null on a Halted build); rebase completion to resumeAt + remaining
    // so the paused span is added onto the schedule. Status → Active (RebaseRates re-adds DrainPerSecond
    // to the ingot drain and the bay draws full power again) and clear HaltedAt.
    public void Apply(ShipBuildResumed @event)
    {
        var index = IndexOfShipBuild(@event.BuildId);
        var build = ShipQueue[index];
        var remaining = build.CompletesAt!.Value - build.HaltedAt!.Value;
        ShipQueue[index] = build with
        {
            Status = ShipBuildStatus.Active,
            CompletesAt = @event.At + remaining,
            HaltedAt = null,
        };
        RebaseRates(@event.At);
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

    // Assembly (#48): pure event factory. The endpoint has already resolved and authorized
    // the ships; an id missing here is a programming error, not a user error.
    public ShipsRemovedFromRoster RemoveShipsFromRoster(Guid fleetId, IReadOnlyList<Guid> shipIds, DateTimeOffset at)
    {
        foreach (var shipId in shipIds)
        {
            if (!Ships.Any(s => s.Id == shipId))
            {
                throw new InvalidOperationException($"Ship {shipId} is not on the roster.");
            }
        }

        return new ShipsRemovedFromRoster(fleetId, shipIds, at);
    }

    // Disband (#48): ships come back carrying the fleet owner's id (D13).
    public ShipsAddedToRoster ReturnShipsToRoster(Guid fleetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at)
        => new(fleetId, ships, at);

    public void Apply(ShipsRemovedFromRoster @event)
    {
        foreach (var shipId in @event.ShipIds)
        {
            var index = Ships.ToList().FindIndex(s => s.Id == shipId);
            if (index >= 0)
            {
                Ships.RemoveAt(index);
            }
        }
        // Roster ships are inert — no rate change, so no RebaseRates.
    }

    public void Apply(ShipsAddedToRoster @event)
    {
        foreach (var ship in @event.Ships)
        {
            Ships.Add(ship);
        }
        // Roster ships are inert — no rate change, so no RebaseRates.
    }
}
