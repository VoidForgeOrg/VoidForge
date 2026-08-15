using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Cascade;

// engine.md "Cascading Events" EDGE cases (#71, Task 2): (5) two independent scheduled checks falling
// due at the SAME instant, and (6) the all-producers-halted "blackout". engine.md L52 requires each
// cascade to resolve "within a single checkpoint" leaving a consistent state.
//
// Implemented as PURE-DOMAIN UNIT tests (no host). The check handlers (CheckPoolDepletedHandler,
// CheckStorageFullHandler, …) are thin, idempotent wrappers — FetchForWriting → Evaluate* →
// AppendMany → SaveChangesAsync — whose validate-on-arrival guard IS the Evaluate* method returning
// []. Each handler's commit path is already exercised individually by Halting/DepletionCascadeTests
// and the storage-halt integration tests, so the NOVEL content of both edge cases is a pure-aggregate
// property: (5) two independent evaluators at one instant compose into one consistent, bounded,
// idempotent (no-double-apply) state, and (6) a fully halted planet's derived energy/rates/queries are
// stable and throw-free. Composing the aggregate in-memory expresses both directly and avoids adding
// runtime-marginal integration tests.
public sealed class CascadeEdgeCaseTests
{
    // Fixed base time so checkpoint/deadline math is deterministic (no DateTimeOffset.UtcNow).
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Colonized planet (deposit 50000, IronOre cap 10000, IronIngot cap 5000, empty buffers) with the
    // caller-supplied operational composition — each BuildingPlaced lands Operational at _at.
    private static Planet Colonized(params BuildingType[] operational)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 0, 0, _at));
        foreach (var type in operational)
        {
            planet.Apply(new BuildingPlaced(type, _at));
        }

        return planet;
    }

    // Edge 5: a depletion deadline (deposit empty → Drill halts) and an output-storage-full deadline
    // (ingot buffer at cap → Refinery halts) fall at the SAME instant. Firing both checks there yields
    // ONE consistent checkpoint: correct halts on DISJOINT slots, pools clamped non-negative and ≤ cap,
    // no throw, and re-firing either check is a validate-on-arrival no-op (no double-apply).
    [Fact]
    public void SimultaneousDepletionAndStorageFullResolveToOneConsistentCheckpoint()
    {
        // Homeworld layout: slot 0 Drill, 1 Refinery, 2 Generator.
        var planet = Colonized(BuildingType.Drill, BuildingType.Refinery, BuildingType.Generator);
        var t = _at.AddSeconds(5000); // the single shared instant both checks fire at.

        // Pin the coincident triggers at t: the finite deposit empty (→ depletion halts the Drill), the
        // ore buffer empty (so the Drill's OUTPUT pool is below cap and is NEVER a storage-full target —
        // the two checks act on DISJOINT slots, making the outcome independent of which commits first),
        // and the ingot buffer full (→ the Refinery halts OutputStorageFull). Rates are left intact so
        // the first Apply's RebaseRates re-derives them honestly.
        planet.IronOreDeposit = planet.IronOreDeposit with { CheckpointValue = 0m, CheckpointTime = t };
        planet.IronOre = planet.IronOre with { CheckpointValue = 0m, CheckpointTime = t };
        planet.IronIngot = planet.IronIngot with { CheckpointValue = planet.IronIngot.StorageCapacity, CheckpointTime = t };

        // Check 1 (depletion): the deposit is empty → the operational Drill (slot 0) halts ResourceDepleted.
        var depletionEvents = planet.EvaluateDepletion(t);
        Assert.Equal(2, depletionEvents.Count);
        Assert.IsType<PlanetResourceDepleted>(depletionEvents[0]);
        var drillHalt = Assert.IsType<BuildingHalted>(depletionEvents[1]);
        Assert.Equal(0, drillHalt.SlotIndex);
        Assert.Equal(HaltReason.ResourceDepleted, drillHalt.Reason);
        planet.Apply((PlanetResourceDepleted)depletionEvents[0]);
        planet.Apply(drillHalt);

        // Check 2 (storage-full) on the post-depletion state: the ingot buffer is at cap → the Refinery
        // (slot 1) halts OutputStorageFull. The already-halted Drill is skipped (not in the Operational set).
        var storageEvents = planet.EvaluateStorageHalts(t);
        var refineryHalt = Assert.IsType<BuildingHalted>(Assert.Single(storageEvents));
        Assert.Equal(1, refineryHalt.SlotIndex);
        Assert.Equal(HaltReason.OutputStorageFull, refineryHalt.Reason);
        planet.Apply(refineryHalt);

        // One consistent checkpoint — correct halts, Generator still Operational.
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[0].Status);
        Assert.Equal(HaltReason.ResourceDepleted, planet.Buildings[0].HaltReason);
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.OutputStorageFull, planet.Buildings[1].HaltReason);
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[2].Status);

        // Pools non-negative and ≤ cap; all production rates settle to 0 (no operational producer left).
        Assert.Equal(0m, planet.IronOreDeposit.GetCurrentValue(t));
        Assert.InRange(planet.IronOre.GetCurrentValue(t), 0m, planet.IronOre.StorageCapacity);
        Assert.InRange(planet.IronIngot.GetCurrentValue(t), 0m, planet.IronIngot.StorageCapacity);
        Assert.Equal(planet.IronIngot.StorageCapacity, planet.IronIngot.GetCurrentValue(t));
        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Equal(0m, planet.IronIngot.Rate);
        Assert.Equal(0m, planet.IronOreDeposit.Rate);

        // Stable + no throw: generation covers only the 5% idle floors (0.05 × (20 + 30) = 2.5 MW), m = 1.
        Assert.Equal(2.5m, planet.GetEnergyConsumptionMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());

        // No double-apply: re-firing either check at the same instant re-derives to [] (validate-on-arrival)
        // — no operational Drill left to deplete, the Refinery is already halted.
        Assert.Empty(planet.EvaluateDepletion(t));
        Assert.Empty(planet.EvaluateStorageHalts(t));
    }

    // Edge 6: the all-producers-halted "blackout" — every Drill halts ResourceDepleted and the Refinery
    // halts InputStarved, leaving only the Generator (a pure source, never a halt target) Operational.
    // The planet must be stable: energy consumption is ONLY the 5% idle floors, all production rates 0,
    // and every read/query is throw-free.
    [Fact]
    public void AllProducersHaltedLeavesPlanetStableOnIdleFloors()
    {
        // Slots: 0 Generator, 1 Drill, 2 Drill, 3 Refinery.
        var planet = Colonized(
            BuildingType.Generator, BuildingType.Drill, BuildingType.Drill, BuildingType.Refinery);

        planet.Apply(new BuildingHalted(1, HaltReason.ResourceDepleted, _at));
        planet.Apply(new BuildingHalted(2, HaltReason.ResourceDepleted, _at));
        planet.Apply(new BuildingHalted(3, HaltReason.InputStarved, _at));

        // Every producer halted; only the Generator stays Operational.
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[0].Status);
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.ResourceDepleted, planet.Buildings[1].HaltReason);
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[2].Status);
        Assert.Equal(HaltReason.ResourceDepleted, planet.Buildings[2].HaltReason);
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[3].Status);
        Assert.Equal(HaltReason.InputStarved, planet.Buildings[3].HaltReason);

        // Energy consumption is ONLY the 5% idle floors of the three halted buildings:
        // 0.05 × (20 + 20 + 30) = 3.5 MW. Generation (100) trivially covers it, so the planet is stable.
        Assert.Equal(3.5m, planet.GetEnergyConsumptionMw());
        Assert.Equal(100m, planet.GetEnergyGenerationMw());
        Assert.Equal(1m, planet.GetProductivityMultiplier());

        // All production rates are 0 — no operational producer feeds or drains anything.
        Assert.Equal(0m, planet.IronOre.Rate);
        Assert.Equal(0m, planet.IronIngot.Rate);
        Assert.Equal(0m, planet.IronOreDeposit.Rate);

        // Reads/queries on the blacked-out planet are stable: no throw, and every predictor/evaluator
        // reports "nothing pending" rather than faulting on the all-zero rates.
        Assert.Null(planet.PredictDepletionDeadline(_at));
        Assert.Null(planet.PredictBufferEmpty(_at));
        Assert.Null(planet.PredictIngotBufferEmpty(_at));
        Assert.Empty(planet.PredictStorageDeadlines(_at));
        Assert.Empty(planet.EvaluateStorageHalts(_at));
        Assert.Empty(planet.EvaluateDepletion(_at));
        Assert.Empty(planet.EvaluateInputStarvation(_at));
        Assert.Empty(planet.EvaluateIngotStarvation(_at));
    }
}
