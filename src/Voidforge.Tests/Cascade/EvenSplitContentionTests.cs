using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Cascade;

// Even-split distribution proofs (#71, Task 2). The MVP resolves competition for a scarce resource
// among several consumers by EVEN SPLIT (game-design/resources.md) — and the engine gets that for free
// because ore and ingots are planet-level SCALAR pools: aggregate consumption is clamped against the
// single shared inflow / buffer scalar, so there is no per-consumer accounting to get wrong. These two
// slices prove that emergent behaviour directly on the aggregate.
//
// Implemented as PURE-DOMAIN UNIT tests (no host): both properties are functions of the aggregate's own
// rate engine (RebaseRates → EffectiveOreConsumption) and its pure predictors/evaluators
// (PredictIngotBufferEmpty, EvaluateInputStarvation, EvaluateIngotStarvation), so an in-memory
// composition asserts them fully and an integration arrangement would be disproportionate (the plan
// flags even-split 7 as unit-friendly; the commit path for the ingot-consumer halts is already covered
// by Halting/IngotStarvationCascadeTests, so proving it again here would be redundant).
[Trait("Category", "Unit")]
public sealed class EvenSplitContentionTests
{
    // Fixed base time so checkpoint/deadline math is deterministic (no DateTimeOffset.UtcNow).
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Storage caps come from PlanetCreated: deposit 50000, IronOre cap 10000, IronIngot cap 5000. Each
    // BuildingPlaced lands Operational at _at (like registration's homeworld seeding), so the caller
    // supplies the operational composition; the starting stores seed the buffers.
    private static Planet Colonized(long oreStored, long ingotStored, params BuildingType[] operational)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), oreStored, ingotStored, _at));
        foreach (var type in operational)
        {
            planet.Apply(new BuildingPlaced(type, _at));
        }

        return planet;
    }

    // Even-split 7: several Refineries competing for one Drill's ore. EffectiveOreConsumption clamps the
    // AGGREGATE refined ore to min(Σ refinery demand, drill inflow) against the single shared IronOre
    // scalar — there is no per-Refinery tracking; the even split IS that scalar clamp, so each Refinery
    // implicitly draws inflow/N.
    //
    // NB on composition: 2 Refineries + 1 Drill (the plan's headline numbers) has demand (10) == inflow
    // (10) at ANY productivity multiplier — both scale by m together — so the min-clamp sits exactly on
    // its boundary and cannot distinguish "clamped to inflow" from "unclamped at demand". To give the
    // proof teeth (and to make "reduced throughput" literally true), this uses 3 Refineries (demand 15)
    // against 1 Drill (inflow 10): the clamp genuinely bites. Two Generators (200 MW gen vs 110 MW draw)
    // keep m == 1 so the energy throttle does not muddy the ore arithmetic; IronIngot.Rate is then still
    // exactly factor × 10 == 20 (the plan's headline number), provably NOT factor × 15 == 30.
    [Fact]
    public void EvenSplitClampsAggregateRefiningToTheSingleSharedDrillInflow()
    {
        var planet = Colonized(
            oreStored: 0,
            ingotStored: 0,
            BuildingType.Drill,
            BuildingType.Refinery,
            BuildingType.Refinery,
            BuildingType.Refinery,
            BuildingType.Generator,
            BuildingType.Generator);

        // Preconditions: full productivity (Generators cover the 110 MW draw) and an empty ore buffer,
        // so EffectiveOreConsumption takes its supply-limited min(demand, inflow) branch.
        Assert.Equal(1m, planet.GetProductivityMultiplier());
        Assert.Equal(0m, planet.IronOre.GetCurrentValue(_at));

        // Aggregate refining clamps to the single 10 ore/s inflow shared across all three Refineries:
        // ingot output = factor × 10 == 20, NOT factor × 15 == 30 (the even-split scalar clamp bites).
        Assert.Equal(BuildingSpecs.RefineryIngotOutputFactor * 10m, planet.IronIngot.Rate);
        Assert.NotEqual(BuildingSpecs.RefineryIngotOutputFactor * 15m, planet.IronIngot.Rate);

        // Net ore rate 0: the shared inflow exactly covers aggregate consumption, so the empty buffer is
        // neither filled nor driven negative — there is no per-Refinery over-draw of the one scalar pool.
        Assert.Equal(0m, planet.IronOre.Rate);

        // From EvaluateInputStarvation: NONE of the Refineries halt. Starvation needs zero inflow AND an
        // empty buffer; here inflow is 10, so every Refinery is supply-LIMITED (reduced throughput), not
        // starved. Even-split means shared scarcity, never a halt while any ore flows.
        Assert.Empty(planet.EvaluateInputStarvation(_at));
    }

    // Even-split 8: a construction drain and a ship-build drain competing for the shared IronIngot buffer.
    // Because both draw from the ONE planet-level ingot scalar, the buffer empties for BOTH at a single
    // PredictIngotBufferEmpty instant, and a single EvaluateIngotStarvation at that instant halts BOTH
    // together (feeding one CheckIngotStarved commit — the commit path itself is covered by
    // Halting/IngotStarvationCascadeTests). Equal drains make the shared drain-down explicit.
    [Fact]
    public void SharedIngotBufferEmptiesForBothConsumersAtTheSameInstant()
    {
        // Generator + Shipyard keep the state a valid, powered game state (m == 1, the ship build is
        // bay-backed). No Refinery, so ingot production is 0 and the buffer only drains.
        var planet = Colonized(oreStored: 0, ingotStored: 100, BuildingType.Generator, BuildingType.Shipyard);

        // An UnderConstruction building (slot 2) draining 1 ingot/s and an Active ship build draining
        // 1 ingot/s — both in flight past the buffer-empty instant (they complete at _at + 100s).
        planet.Apply(new BuildingConstructionStarted(2, BuildingType.Drill, _at, _at.AddSeconds(100), DrainPerSecond: 1m));
        var shipId = Guid.NewGuid();
        planet.Apply(new ShipConstructionQueued(shipId, ShipType.ColonyShip, _at, DrainPerSecond: 1m, BuildDurationSeconds: 100m));
        planet.Apply(new ShipConstructionStarted(shipId, _at, _at.AddSeconds(100)));

        // The two drains share the one buffer: net ingot rate is −(1 + 1) = −2/s, productivity full.
        Assert.Equal(1m, planet.GetProductivityMultiplier());
        Assert.Equal(-2m, planet.IronIngot.Rate);

        // One empty instant for the shared scalar buffer: 100 ingots ÷ 2/s = 50s.
        var deadline = planet.PredictIngotBufferEmpty(_at);
        Assert.NotNull(deadline);
        Assert.Equal(ResourceType.IronIngot, deadline.Resource);
        Assert.Equal(_at.AddSeconds(50), deadline.At);
        var emptyAt = deadline.At;

        // Just before that instant the shared buffer still has ingots, so NEITHER consumer halts.
        Assert.Empty(planet.EvaluateIngotStarvation(emptyAt.AddSeconds(-1)));

        // At the empty instant a single EvaluateIngotStarvation halts BOTH consumers together — one
        // ConstructionHalted and one ShipBuildHalted, each stamped at the SAME emptyAt.
        var halts = planet.EvaluateIngotStarvation(emptyAt);
        Assert.Equal(2, halts.Count);
        var constructionHalt = Assert.IsType<ConstructionHalted>(halts.Single(e => e is ConstructionHalted));
        Assert.Equal(2, constructionHalt.SlotIndex);
        Assert.Equal(emptyAt, constructionHalt.At);
        var shipHalt = Assert.IsType<ShipBuildHalted>(halts.Single(e => e is ShipBuildHalted));
        Assert.Equal(shipId, shipHalt.BuildId);
        Assert.Equal(emptyAt, shipHalt.At);
    }
}
