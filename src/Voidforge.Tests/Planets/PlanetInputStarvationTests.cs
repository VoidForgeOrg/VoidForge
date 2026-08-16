using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

// Refinery buffer-drain + input starvation (#70, Task 3): a Refinery draws down the STORED IronOre
// buffer when drill inflow is short, and halts InputStarved only when BOTH inflow and buffer are
// exhausted. Mirrors PlanetDepletionTests' fixed-base-time, direct-Apply style so checkpoint/deadline
// math is deterministic (no DateTimeOffset.UtcNow).
public sealed class PlanetInputStarvationTests
{
    private static readonly DateTimeOffset _base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Storage caps come from PlanetCreated: deposit 50000, IronOre 10000, IronIngot 5000. Colonization
    // seeds a NON-empty IronOre buffer of 500, so the buffer-drain branch is exercised.
    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 7, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, _base));
        return planet;
    }

    private static void Place(Planet planet, DateTimeOffset at, params BuildingType[] types)
    {
        foreach (var type in types)
        {
            planet.Apply(new BuildingPlaced(type, at));
        }
    }

    // Pin the stored IronOre buffer to empty without changing composition (the Rate stays as
    // RebaseRates derived it, which is what the starvation check runs against).
    private static void EmptyOreBuffer(Planet planet, DateTimeOffset at) =>
        planet.IronOre = planet.IronOre with { CheckpointValue = 0m, CheckpointTime = at };

    // (a) A Refinery with no drill inflow but a non-empty buffer runs at full demand and DRAINS the
    // buffer: net ore rate goes negative and GetCurrentValue falls over time.
    [Fact]
    public void RefineryDrawsBufferAtNegativeRateWhenNoDrillInflow()
    {
        var planet = CreateColonizedPlanet();
        // Generator + Refinery, no Drill: inflow 0, demand 5, buffer 500 > 0 → net ore = -5.
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery);

        Assert.Equal(-5m, planet.IronOre.Rate);
        Assert.Equal(10m, planet.IronIngot.Rate); // 2 x 5 consumed from the buffer.

        var atBase = planet.IronOre.GetCurrentValue(_base);
        var at50 = planet.IronOre.GetCurrentValue(_base.AddSeconds(50));
        var at100 = planet.IronOre.GetCurrentValue(_base.AddSeconds(100));
        Assert.Equal(500m, atBase);
        Assert.Equal(250m, at50); // 500 - 5 * 50.
        Assert.Equal(0m, at100);  // 500 - 5 * 100, floored at 0.
        Assert.True(at50 < atBase && at100 < at50, "the buffer must strictly decrease while draining.");
    }

    // (a') Buffer-drain also applies when there IS inflow but demand exceeds it: net ore = inflow -
    // demand (still negative), the buffer covering the shortfall.
    [Fact]
    public void RefineryDrawsBufferWhenDemandExceedsInflow()
    {
        var planet = CreateColonizedPlanet();
        // 2 Generators (200 MW) + Drill (20) + 3 Refineries (90) → m = 1. inflow 10, demand 15.
        Place(planet, _base, BuildingType.Generator, BuildingType.Generator, BuildingType.Drill,
            BuildingType.Refinery, BuildingType.Refinery, BuildingType.Refinery);

        Assert.Equal(-5m, planet.IronOre.Rate); // 10 inflow - 15 demand.
        Assert.True(
            planet.IronOre.GetCurrentValue(_base.AddSeconds(20)) < planet.IronOre.GetCurrentValue(_base),
            "the buffer must drain while demand exceeds inflow.");
    }

    // (b) PredictBufferEmpty returns now + current / (-Rate) for a draining buffer, and null when the
    // buffer is not draining (net rate >= 0) or already empty.
    [Fact]
    public void PredictBufferEmptyReturnsCurrentOverDrainRate()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery); // rate -5, buffer 500.

        var deadline = planet.PredictBufferEmpty(_base);
        Assert.NotNull(deadline);
        Assert.Equal(ResourceType.IronOre, deadline.Resource);
        Assert.Equal(_base.AddSeconds(100), deadline.At); // 500 / 5 = 100s.

        // Net ore rate >= 0 (a Drill supplies the whole demand) → not draining → no deadline.
        var notDraining = CreateColonizedPlanet();
        Place(notDraining, _base, BuildingType.Generator, BuildingType.Drill, BuildingType.Refinery);
        Assert.True(notDraining.IronOre.Rate >= 0);
        Assert.Null(notDraining.PredictBufferEmpty(_base));

        // An already-empty buffer yields no deadline even with a negative rate.
        var empty = CreateColonizedPlanet();
        Place(empty, _base, BuildingType.Generator, BuildingType.Refinery);
        EmptyOreBuffer(empty, _base);
        Assert.Null(empty.PredictBufferEmpty(_base));
    }

    // (c) EvaluateInputStarvation halts a Refinery only when inflow is 0 AND the buffer is empty.
    [Fact]
    public void EvaluateInputStarvationHaltsRefineryWhenNoInflowAndEmptyBuffer()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery); // no Drill → zero inflow.
        EmptyOreBuffer(planet, _base);

        var events = planet.EvaluateInputStarvation(_base);

        var halt = Assert.IsType<BuildingHalted>(Assert.Single(events));
        Assert.Equal(1, halt.SlotIndex); // Generator slot 0, Refinery slot 1.
        Assert.Equal(HaltReason.InputStarved, halt.Reason);
        Assert.Equal(_base, halt.At);
    }

    // (c') A Refinery running at REDUCED throughput (inflow > 0 but below demand) is NOT starved, even
    // with an empty buffer — starvation is strictly zero-inflow AND zero-buffer.
    [Fact]
    public void EvaluateInputStarvationEmitsNothingWhenSomeInflow()
    {
        var planet = CreateColonizedPlanet();
        // 2 Generators + Drill (inflow 10) + 3 Refineries (demand 15), buffer emptied: inflow-limited
        // throughput, not starvation.
        Place(planet, _base, BuildingType.Generator, BuildingType.Generator, BuildingType.Drill,
            BuildingType.Refinery, BuildingType.Refinery, BuildingType.Refinery);
        EmptyOreBuffer(planet, _base);

        Assert.Empty(planet.EvaluateInputStarvation(_base));
    }

    // (c''') Rate rebase when the buffer empties at REDUCED throughput (#70 fix): a Refinery below demand
    // drains the buffer without starving (inflow > 0 → EvaluateInputStarvation emits no halt). Once the
    // buffer is empty the ingot PRODUCTION rate must fall from factor*demand to the sustainable
    // factor*inflow — otherwise ingots are fabricated at 30/s when only 20/s is physically producible.
    // EvaluateOreBufferEmptied emits a composition-neutral rebase that re-clamps consumption to the inflow.
    [Fact]
    public void OreBufferEmptyingRebasesRatesToSustainableInflow()
    {
        var planet = CreateColonizedPlanet();
        // 2 Generators + Drill (inflow 10) + 3 Refineries (demand 15), buffer 500: net ore -5, ingots 2*15=30.
        Place(planet, _base, BuildingType.Generator, BuildingType.Generator, BuildingType.Drill,
            BuildingType.Refinery, BuildingType.Refinery, BuildingType.Refinery);
        Assert.Equal(-5m, planet.IronOre.Rate);
        Assert.Equal(30m, planet.IronIngot.Rate);   // full demand while the buffer lasts.

        // The buffer empties 100s later (500 / 5). No starvation halt — inflow 10 > 0...
        var emptyAt = _base.AddSeconds(100);
        Assert.Equal(0m, planet.IronOre.GetCurrentValue(emptyAt));
        Assert.Empty(planet.EvaluateInputStarvation(emptyAt));

        // ...but the buffer emptied, so a single composition-neutral rebase re-clamps the rates.
        var rebase = Assert.IsType<IronOreBufferEmptied>(Assert.Single(planet.EvaluateOreBufferEmptied(emptyAt)));
        Assert.Equal(emptyAt, rebase.At);
        planet.Apply(rebase);

        Assert.Equal(0m, planet.IronOre.Rate);       // inflow 10 - sustainable consumption 10.
        Assert.Equal(20m, planet.IronIngot.Rate);    // 2 * min(demand 15, inflow 10) — no more over-production.

        // Idempotent + terminal: re-running the check emits nothing and PredictBufferEmpty is null (no loop).
        Assert.Empty(planet.EvaluateOreBufferEmptied(emptyAt));
        Assert.Null(planet.PredictBufferEmpty(emptyAt));
    }

    // A non-empty buffer (a superseded/early check) yields no rebase, and neither does an already-clamped
    // planet — so a replayed or duplicate CheckInputStarved is an idempotent no-op.
    [Fact]
    public void EvaluateOreBufferEmptiedEmitsNothingWhenBufferHasOreOrNotDraining()
    {
        var draining = CreateColonizedPlanet();
        Place(draining, _base, BuildingType.Generator, BuildingType.Refinery); // rate -5, buffer 500 > 0.
        Assert.Empty(draining.EvaluateOreBufferEmptied(_base));                 // buffer not yet empty.

        var notDraining = CreateColonizedPlanet();
        Place(notDraining, _base, BuildingType.Generator, BuildingType.Drill, BuildingType.Refinery);
        EmptyOreBuffer(notDraining, _base);
        Assert.True(notDraining.IronOre.Rate >= 0);
        Assert.Empty(notDraining.EvaluateOreBufferEmptied(_base));              // empty but not draining.
    }

    // (c'') A Refinery with zero inflow but ore still in the buffer is NOT starved — it is draining it.
    [Fact]
    public void EvaluateInputStarvationEmitsNothingWhileBufferHasOre()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery); // zero inflow, buffer 500.

        Assert.Empty(planet.EvaluateInputStarvation(_base));
    }

    // (d) The mini-cascade: a Refinery drains the buffer (producing ingots), the buffer empties, and
    // applying the InputStarved halt stops the Refinery so ingot production drops to 0.
    [Fact]
    public void ApplyingInputStarvedHaltStopsRefineryAndIngotProduction()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery); // buffer 500 drains at -5.

        // While draining the buffer, the Refinery produces ingots at 10/s.
        Assert.Equal(10m, planet.IronIngot.Rate);

        // The buffer empties 100s later; at that instant the Refinery is input-starved and halts.
        var emptyAt = _base.AddSeconds(100);
        Assert.Equal(0m, planet.IronOre.GetCurrentValue(emptyAt));
        var halt = Assert.IsType<BuildingHalted>(Assert.Single(planet.EvaluateInputStarvation(emptyAt)));
        planet.Apply(halt);

        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.InputStarved, planet.Buildings[1].HaltReason);
        Assert.Equal(0m, planet.IronIngot.Rate); // No operational Refinery → ingot production stops.
        Assert.Equal(0m, planet.IronOre.Rate);    // No consumer left → net ore rate back to 0.
    }

    // (e) EvaluateInputStarvationResumes un-halts an InputStarved Refinery once ore returns — here via a
    // new Drill restoring inflow (the CompleteBuildingConstructionHandler-wired case, #70). A planet
    // still dry (no inflow, empty buffer) resumes nothing; only InputStarved buildings are un-halted.
    [Fact]
    public void EvaluateInputStarvationResumesRefineryOnceDrillInflowReturns()
    {
        var planet = CreateColonizedPlanet();
        // Starve it: Generator + Refinery, no Drill, buffer emptied → the Refinery halts InputStarved.
        Place(planet, _base, BuildingType.Generator, BuildingType.Refinery);
        EmptyOreBuffer(planet, _base);
        var halt = Assert.IsType<BuildingHalted>(Assert.Single(planet.EvaluateInputStarvation(_base)));
        planet.Apply(halt);
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.InputStarved, planet.Buildings[1].HaltReason);

        // Still dry (zero inflow, empty buffer): nothing resumes.
        Assert.Empty(planet.EvaluateInputStarvationResumes(_base));

        // A new Drill restores ore inflow (slot 2, immediately Operational): the Refinery resumes.
        Place(planet, _base, BuildingType.Drill);
        Assert.True(planet.IronOre.Rate > 0m, "inflow must return once the Drill is operational.");

        var resume = Assert.IsType<BuildingResumed>(Assert.Single(planet.EvaluateInputStarvationResumes(_base)));
        Assert.Equal(1, resume.SlotIndex); // the Refinery slot.
        planet.Apply(resume);
        Assert.Equal(BuildingStatus.Operational, planet.Buildings[1].Status);
        Assert.Null(planet.Buildings[1].HaltReason);
    }
}
