using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Storage-halting concern of the Planet aggregate (#69): output-storage-full halt/resume
// evaluation, fill-time prediction, and the halt/resume Apply methods. Same partial class as
// Planet.cs, so the private RebaseRates is accessible here.
public sealed partial class Planet
{
    // EvaluateStorageHalts: an Operational producer whose OUTPUT pool is at capacity halts.
    public IReadOnlyList<object> EvaluateStorageHalts(DateTimeOffset now)
    {
        var events = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.Operational) continue;
            var produced = BuildingSpecs.ProducedResource(slot.Type);
            if (produced is null) continue;
            var pool = PoolFor(produced.Value);
            if (pool.GetCurrentValue(now) >= pool.StorageCapacity)
                events.Add(new BuildingHalted(i, HaltReason.OutputStorageFull, now));
        }
        return events;
    }

    // EvaluateStorageResumes: a building halted OutputStorageFull whose output pool now has
    // headroom resumes.
    public IReadOnlyList<object> EvaluateStorageResumes(DateTimeOffset now)
        => EvaluateStorageResumes(now, 0m, 0m);

    // EvaluateStorageResumesAfterLoad (D6, #69): the resumes a cargo load of (loadedOre,
    // loadedIngot) off this planet's storage would trigger, evaluated against each output
    // pool's POST-load value WITHOUT mutating this aggregate. The assemble endpoint calls this
    // on a Marten FetchForWriting aggregate that MUST stay pristine until SaveChangesAsync:
    // with UseIdentityMapForAggregates (Program.cs), Marten re-applies the appended
    // CargoLoadedFromStorage onto this very instance at commit, so altering the pools here
    // would double-count the load. Subtracting the pending load reproduces exactly what
    // Apply(CargoLoadedFromStorage) leaves behind (checkpoint-at-now then clamp).
    public IReadOnlyList<object> EvaluateStorageResumesAfterLoad(
        decimal loadedOre, decimal loadedIngot, DateTimeOffset now)
        => EvaluateStorageResumes(now, loadedOre, loadedIngot);

    // Shared core: a building halted OutputStorageFull resumes once its output pool — after
    // subtracting any pending (not-yet-committed) cargo load — has headroom below capacity.
    private List<object> EvaluateStorageResumes(DateTimeOffset now, decimal pendingOre, decimal pendingIngot)
    {
        var events = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.Halted || slot.HaltReason != HaltReason.OutputStorageFull) continue;
            var produced = BuildingSpecs.ProducedResource(slot.Type);
            if (produced is null) continue;
            var pool = PoolFor(produced.Value);
            var pending = produced.Value == ResourceType.IronOre ? pendingOre : pendingIngot;
            var projected = Math.Clamp(pool.GetCurrentValue(now) - pending, 0, pool.StorageCapacity);
            if (projected < pool.StorageCapacity)
                events.Add(new BuildingResumed(i, now));
        }
        return events;
    }

    // PredictStorageDeadlines: per pool with positive net rate and below capacity, time-to-full.
    public IReadOnlyList<StorageDeadline> PredictStorageDeadlines(DateTimeOffset now)
    {
        var deadlines = new List<StorageDeadline>();
        foreach (var (resource, pool) in new[] { (ResourceType.IronOre, IronOre), (ResourceType.IronIngot, IronIngot) })
        {
            if (pool.Rate <= 0) continue;
            var current = pool.GetCurrentValue(now);
            if (current >= pool.StorageCapacity) continue;
            var seconds = (double)((pool.StorageCapacity - current) / pool.Rate);
            deadlines.Add(new StorageDeadline(resource, now.AddSeconds(seconds)));
        }
        return deadlines;
    }

    private ResourcePool PoolFor(ResourceType r) => r == ResourceType.IronOre ? IronOre : IronIngot;

    public void Apply(BuildingHalted @event)
    {
        var slot = Buildings[@event.SlotIndex];
        Buildings[@event.SlotIndex] = slot with { Status = BuildingStatus.Halted, HaltReason = @event.Reason };
        RebaseRates(@event.At);
    }

    public void Apply(BuildingResumed @event)
    {
        var slot = Buildings[@event.SlotIndex];
        Buildings[@event.SlotIndex] = slot with { Status = BuildingStatus.Operational, HaltReason = null };
        RebaseRates(@event.At);
    }
}
