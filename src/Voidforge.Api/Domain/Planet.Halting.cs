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

    // EvaluateDepletion (#70): once the finite ore deposit is empty, every Operational Drill halts
    // PERMANENTLY. Emits PlanetResourceDepleted first (pins the deposit to 0), then one
    // BuildingHalted(ResourceDepleted) per operational Drill — each Apply is independent, so the
    // order is purely for readability. Permanence is free: EvaluateStorageResumes only un-halts
    // HaltReason.OutputStorageFull, so a ResourceDepleted drill is skipped by every resume evaluator.
    // Validate-on-arrival no-op ([]) if the deposit still has ore or no Drill is operational (a
    // superseded scheduled CheckPoolDepleted).
    public IReadOnlyList<object> EvaluateDepletion(DateTimeOffset at)
    {
        if (IronOreDeposit.GetCurrentValue(at) > 0) return [];

        var drillHalts = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.Operational || slot.Type != BuildingType.Drill) continue;
            drillHalts.Add(new BuildingHalted(i, HaltReason.ResourceDepleted, at));
        }

        if (drillHalts.Count == 0) return [];

        var events = new List<object> { new PlanetResourceDepleted(ResourceType.IronOre, at) };
        events.AddRange(drillHalts);
        return events;
    }

    // PredictDepletionDeadline (#70): the instant the finite ore deposit empties at the current
    // extraction rate — now + remaining / extractionRate — or null when it is not draining (no
    // operational Drill → deposit Rate 0) or already empty. IronOreDeposit.Rate is -oreInflow, so
    // extractionRate = -Rate is the positive drain. Symmetric to PredictStorageDeadlines; feeds the
    // CheckPoolDepleted scheduling wired in Task 4.
    public StorageDeadline? PredictDepletionDeadline(DateTimeOffset now)
    {
        var extractionRate = -IronOreDeposit.Rate;
        if (extractionRate <= 0) return null;
        var remaining = IronOreDeposit.GetCurrentValue(now);
        if (remaining <= 0) return null;
        var seconds = (double)(remaining / extractionRate);
        return new StorageDeadline(ResourceType.IronOre, now.AddSeconds(seconds));
    }

    // EvaluateInputStarvation (#70): an Operational Refinery halts InputStarved only when the planet
    // has NO ore to feed it — zero drill inflow (no operational drill producing) AND an empty IronOre
    // buffer. A refinery running at REDUCED throughput (some inflow, or a draining-but-still-nonempty
    // buffer) is NOT starved and is left alone. Emits one BuildingHalted(InputStarved) per starved
    // Refinery; [] otherwise (also the validate-on-arrival no-op for a superseded CheckInputStarved
    // where ore has returned). oreInflow comes from CurrentOreInflow() so it matches RebaseRates.
    public IReadOnlyList<object> EvaluateInputStarvation(DateTimeOffset at)
    {
        if (CurrentOreInflow() > 0 || IronOre.GetCurrentValue(at) > 0) return [];

        var halts = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.Operational || slot.Type != BuildingType.Refinery) continue;
            halts.Add(new BuildingHalted(i, HaltReason.InputStarved, at));
        }
        return halts;
    }

    // EvaluateOreBufferEmptied (#70 rate-rebase): a Refinery running at REDUCED throughput drains the
    // stored buffer without ever starving — 0 < inflow < demand, so EvaluateInputStarvation emits no halt
    // (inflow > 0) and no composition event fires when the buffer empties. But EffectiveOreConsumption
    // switches from full demand to min(demand, inflow) exactly when the buffer hits 0, so the rates MUST
    // be re-derived at that instant: otherwise IronIngot.Rate stays at factor*demand and fabricates ingots
    // (and IronOre.Rate stays negative though the pool floors at 0). Emits one composition-neutral
    // IronOreBufferEmptied when the buffer is empty AND still draining (Rate < 0). [] otherwise: a non-empty
    // buffer means a superseded check (ore returned), and Rate >= 0 means already re-clamped — so a replayed
    // or duplicate CheckInputStarved is an idempotent no-op that never loops. Scheduled at the predicted
    // empty instant by PredictBufferEmpty, alongside the starvation halt it complements.
    public IReadOnlyList<object> EvaluateOreBufferEmptied(DateTimeOffset at)
    {
        if (IronOre.GetCurrentValue(at) > 0 || IronOre.Rate >= 0) return [];
        return [new IronOreBufferEmptied(at)];
    }

    // EvaluateInputStarvationResumes (#70): a Refinery halted InputStarved resumes once ore is
    // genuinely restored — either drill inflow returned (a new/resumed Drill → CurrentOreInflow() > 0)
    // or the stored buffer was refilled (a cargo delivery → IronOre.GetCurrentValue(at) > 0). Depletion
    // is permanent, so most starvation never resumes; this fires only on the ore-restoring commits.
    // Composition-driven and symmetric to EvaluateStorageResumes (it only un-halts InputStarved, so a
    // ResourceDepleted/OutputStorageFull building is skipped): emits one BuildingResumed per resumable
    // Refinery, [] when the planet is still dry (no inflow AND an empty buffer).
    public IReadOnlyList<object> EvaluateInputStarvationResumes(DateTimeOffset at)
    {
        if (CurrentOreInflow() <= 0 && IronOre.GetCurrentValue(at) <= 0) return [];

        var resumes = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.Halted || slot.HaltReason != HaltReason.InputStarved) continue;
            resumes.Add(new BuildingResumed(i, at));
        }
        return resumes;
    }

    // EvaluateIngotStarvation (#83): the ingot-consumer mirror of EvaluateInputStarvation. An in-flight
    // build pauses only in the clean zero-production case — NO ingots being produced
    // (IngotProduction(at) == 0) AND an empty IronIngot buffer. A build still drawing from a non-empty
    // buffer, or with refineries producing, is left alone (mirrors the zero-inflow-AND-zero-buffer ore
    // test — avoids oscillation and the ill-defined even-split of fixed-drain consumers; production > 0
    // but below drain retains today's clamp-only behaviour). Emits one ConstructionHalted per
    // UnderConstruction building AND one ShipBuildHalted per Active ship build (both ingot consumers,
    // gated by the same zero-production + empty-buffer test — a single planet-level ingot scalar drives
    // both); [] otherwise (also the validate-on-arrival no-op for a superseded CheckIngotStarved where
    // ingots returned).
    public IReadOnlyList<object> EvaluateIngotStarvation(DateTimeOffset at)
    {
        if (IngotProduction(at) > 0 || IronIngot.GetCurrentValue(at) > 0) return [];

        var halts = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            var slot = Buildings[i];
            if (slot.Status != BuildingStatus.UnderConstruction) continue;
            halts.Add(new ConstructionHalted(i, at));
        }

        foreach (var build in ShipQueue)
        {
            if (build.Status != ShipBuildStatus.Active) continue;
            halts.Add(new ShipBuildHalted(build.Id, at));
        }

        return halts;
    }

    // EvaluateIngotStarvationResumes (#83): the ingot-consumer mirror of EvaluateInputStarvationResumes.
    // Once ingots are genuinely restored — refineries producing again (IngotProduction(at) > 0) or the
    // buffer refilled (IronIngot.GetCurrentValue(at) > 0) — every paused consumer resumes: one
    // ConstructionResumed per ConstructionHalted building AND one ShipBuildResumed per Halted ship build.
    // Both Apply methods recompute CompletesAt off the captured HaltedAt. [] while still starved (no
    // production AND an empty buffer). Composition-driven (chained off the ore-return commit in Task 4),
    // not check-driven; symmetric to EvaluateInputStarvationResumes.
    public IReadOnlyList<object> EvaluateIngotStarvationResumes(DateTimeOffset at)
    {
        if (IngotProduction(at) <= 0 && IronIngot.GetCurrentValue(at) <= 0) return [];

        var resumes = new List<object>();
        for (var i = 0; i < Buildings.Count; i++)
        {
            if (Buildings[i].Status != BuildingStatus.ConstructionHalted) continue;
            resumes.Add(new ConstructionResumed(i, at));
        }

        foreach (var build in ShipQueue)
        {
            if (build.Status != ShipBuildStatus.Halted) continue;
            resumes.Add(new ShipBuildResumed(build.Id, at));
        }

        return resumes;
    }

    // PredictIngotBufferEmpty (#83): the instant the stored IronIngot buffer empties while construction
    // (buildings + ship builds) drains it faster than refineries produce (IronIngot.Rate < 0) — now +
    // current / (−Rate) — or null when it is not draining (Rate >= 0) or already empty. The ingot mirror
    // of PredictBufferEmpty; feeds the CheckIngotStarved scheduling wired in Task 3.
    public StorageDeadline? PredictIngotBufferEmpty(DateTimeOffset now)
    {
        if (IronIngot.Rate >= 0) return null;
        var current = IronIngot.GetCurrentValue(now);
        if (current <= 0) return null;
        var seconds = (double)(current / -IronIngot.Rate);
        return new StorageDeadline(ResourceType.IronIngot, now.AddSeconds(seconds));
    }

    // PredictBufferEmpty (#70): the instant the stored IronOre buffer empties while a refinery drains
    // it faster than drills supply (IronOre.Rate < 0) — now + current / (−Rate) — or null when the
    // buffer is not draining (Rate >= 0) or is already empty. Symmetric to PredictStorageDeadlines'
    // time-to-full; feeds the CheckInputStarved scheduling wired in Task 4.
    public StorageDeadline? PredictBufferEmpty(DateTimeOffset now)
    {
        if (IronOre.Rate >= 0) return null;
        var current = IronOre.GetCurrentValue(now);
        if (current <= 0) return null;
        var seconds = (double)(current / -IronOre.Rate);
        return new StorageDeadline(ResourceType.IronOre, now.AddSeconds(seconds));
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

    // Apply(ConstructionHalted) (#83): pause an in-flight build. Status → ConstructionHalted (leaves the
    // UnderConstruction set, so RebaseRates' constructionDrain filter drops its drain to 0 — the build
    // consumes nothing while paused) and stamp HaltedAt. CompletesAt and ConstructionDrainPerSecond are
    // KEPT: resume needs CompletesAt − HaltedAt for the remaining work, and the kept drain is restored on
    // resume (it is excluded from the drain sum while ConstructionHalted, so keeping it is harmless).
    public void Apply(ConstructionHalted @event)
    {
        var slot = Buildings[@event.SlotIndex];
        Buildings[@event.SlotIndex] = slot with
        {
            Status = BuildingStatus.ConstructionHalted,
            HaltedAt = @event.At,
        };
        RebaseRates(@event.At);
    }

    // Apply(ConstructionResumed) (#83): resume a paused build. The remaining work captured at halt was
    // CompletesAt − HaltedAt (both non-null on a ConstructionHalted slot); rebase completion to
    // resumeAt + remaining so the paused span is added onto the schedule. Status → UnderConstruction
    // (RebaseRates re-adds ConstructionDrainPerSecond to the ingot drain) and clear HaltedAt.
    public void Apply(ConstructionResumed @event)
    {
        var slot = Buildings[@event.SlotIndex];
        var remaining = slot.CompletesAt!.Value - slot.HaltedAt!.Value;
        Buildings[@event.SlotIndex] = slot with
        {
            Status = BuildingStatus.UnderConstruction,
            CompletesAt = @event.At + remaining,
            HaltedAt = null,
        };
        RebaseRates(@event.At);
    }

    // Apply(PlanetResourceDepleted) (#70): pin the deposit to empty at the depletion instant. The
    // drill halts ride alongside as separate BuildingHalted events (see EvaluateDepletion), each
    // with its own Apply that drops the drill from the Operational set — so RebaseRates re-derives
    // oreInflow → 0 and the deposit's drain Rate → 0. No RebaseRates here: this event does not
    // change building composition, it only checkpoints the deposit value.
    public void Apply(PlanetResourceDepleted @event)
    {
        IronOreDeposit = IronOreDeposit.Checkpoint(@event.At) with { CheckpointValue = 0m };
    }

    // Apply(IronOreBufferEmptied) (#70 rate-rebase): composition-neutral. RebaseRates re-reads the
    // now-empty IronOre buffer, so EffectiveOreConsumption clamps refinery consumption to the sustainable
    // inflow — IronOre.Rate → 0 (no longer draining) and IronIngot.Rate → factor*inflow (stops
    // over-producing). Idempotent: a second apply at the same instant re-derives the identical rates.
    public void Apply(IronOreBufferEmptied @event) => RebaseRates(@event.At);
}
