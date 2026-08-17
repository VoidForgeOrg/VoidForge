using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

// Depletion cascade (#70, Task 2): once the finite ore deposit empties, every operational Drill
// halts PERMANENTLY (HaltReason.ResourceDepleted). Mirrors PlanetHaltingTests' fixed-base-time,
// direct-Apply style so checkpoint/deadline math is deterministic (no DateTimeOffset.UtcNow).
[Trait("Category", "Unit")]
public sealed class PlanetDepletionTests
{
    private static readonly DateTimeOffset _base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Storage caps come from PlanetCreated: deposit 50000, IronOre 10000, IronIngot 5000.
    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
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

    // Pin the deposit's remaining ore low so it drains to empty shortly after `at`, without
    // touching the drain Rate that RebaseRates derived from the drill composition.
    private static void SetDepositRemaining(Planet planet, decimal remaining, DateTimeOffset at) =>
        planet.IronOreDeposit = planet.IronOreDeposit with { CheckpointValue = remaining, CheckpointTime = at };

    private static void ApplyAll(Planet planet, IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case PlanetResourceDepleted depleted:
                    planet.Apply(depleted);
                    break;
                case BuildingHalted halted:
                    planet.Apply(halted);
                    break;
                default:
                    Assert.Fail($"Unexpected event type {e.GetType().Name}");
                    break;
            }
        }
    }

    // (a) At an instant where the deposit is empty, EvaluateDepletion emits PlanetResourceDepleted
    // first, then one BuildingHalted(ResourceDepleted) per operational Drill.
    [Fact]
    public void EvaluateDepletionEmitsDepletedEventAndHaltPerOperationalDrillWhenDepositEmpty()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Drill, BuildingType.Drill);
        // Two operational Drills → extraction 20/s; near-empty at _base drains past 0 well before +50s.
        SetDepositRemaining(planet, 100m, _base);
        var depletedAt = _base.AddSeconds(50);
        Assert.Equal(0m, planet.IronOreDeposit.GetCurrentValue(depletedAt));

        var events = planet.EvaluateDepletion(depletedAt);

        Assert.Equal(3, events.Count);
        var depleted = Assert.IsType<PlanetResourceDepleted>(events[0]);
        Assert.Equal(ResourceType.IronOre, depleted.Resource);
        Assert.Equal(depletedAt, depleted.At);

        var halts = events.Skip(1).Cast<BuildingHalted>().ToList();
        Assert.Equal(2, halts.Count);
        Assert.All(halts, h => Assert.Equal(HaltReason.ResourceDepleted, h.Reason));
        Assert.All(halts, h => Assert.Equal(depletedAt, h.At));
        Assert.Equal(1, halts[0].SlotIndex); // Generator is slot 0, Drills are slots 1 and 2.
        Assert.Equal(2, halts[1].SlotIndex);
    }

    // (a') Validate-on-arrival no-op: a deposit with ore left, or with no operational Drill, emits
    // nothing (a superseded scheduled CheckPoolDepleted).
    [Fact]
    public void EvaluateDepletionEmitsNothingWhenDepositHasOreOrNoOperationalDrill()
    {
        var withOre = CreateColonizedPlanet();
        Place(withOre, _base, BuildingType.Generator, BuildingType.Drill);
        SetDepositRemaining(withOre, 1000m, _base); // still draining, not empty yet.
        Assert.Empty(withOre.EvaluateDepletion(_base));

        var noDrill = CreateColonizedPlanet();
        Place(noDrill, _base, BuildingType.Generator);
        SetDepositRemaining(noDrill, 0m, _base); // empty, but nothing extracting.
        Assert.Empty(noDrill.EvaluateDepletion(_base));
    }

    // (b) Applying the depletion events halts each Drill (Status/HaltReason), and the halted Drills
    // leave the Operational set so ore inflow and the deposit's drain rate both drop to 0.
    [Fact]
    public void ApplyingDepletionEventsHaltsDrillsAndZeroesExtraction()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Drill);
        SetDepositRemaining(planet, 100m, _base);
        var depletedAt = _base.AddSeconds(50);

        // Before depletion: one operational Drill produces ore at 10/s and drains the deposit at -10/s.
        Assert.Equal(10m, planet.IronOre.Rate);
        Assert.Equal(-10m, planet.IronOreDeposit.Rate);

        ApplyAll(planet, planet.EvaluateDepletion(depletedAt));

        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.ResourceDepleted, planet.Buildings[1].HaltReason);
        Assert.Equal(0m, planet.IronOre.Rate);          // Halted Drill left the Operational set.
        Assert.Equal(0m, planet.IronOreDeposit.Rate);   // oreInflow 0 → deposit no longer drains.
        Assert.Equal(0m, planet.IronOreDeposit.GetCurrentValue(depletedAt));
    }

    // (c) Permanence: a depletion-halted Drill is never resumed by a storage-resume evaluation
    // (that filter only un-halts OutputStorageFull), even with the ore pool wide open. It stays Halted.
    [Fact]
    public void DepletedDrillStaysHaltedAndStorageResumeEvaluationSkipsIt()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Drill);
        SetDepositRemaining(planet, 100m, _base);
        var depletedAt = _base.AddSeconds(50);
        ApplyAll(planet, planet.EvaluateDepletion(depletedAt));
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);

        // The IronOre buffer never filled, so a storage-resume evaluator would resume an
        // OutputStorageFull drill here — but this drill is ResourceDepleted, so it is skipped.
        var laterTime = depletedAt.AddSeconds(3600);
        Assert.Empty(planet.EvaluateStorageResumes(laterTime));
        Assert.Equal(BuildingStatus.Halted, planet.Buildings[1].Status);
        Assert.Equal(HaltReason.ResourceDepleted, planet.Buildings[1].HaltReason);
    }

    // (d) PredictDepletionDeadline returns now + remaining / extractionRate for a draining deposit,
    // and null when the deposit is not draining (no operational Drill → deposit Rate 0).
    [Fact]
    public void PredictDepletionDeadlineReturnsRemainingOverExtractionRate()
    {
        var planet = CreateColonizedPlanet();
        Place(planet, _base, BuildingType.Generator, BuildingType.Drill); // extraction 10/s.
        SetDepositRemaining(planet, 1000m, _base);

        var deadline = planet.PredictDepletionDeadline(_base);

        Assert.NotNull(deadline);
        Assert.Equal(ResourceType.IronOre, deadline.Resource);
        Assert.Equal(_base.AddSeconds(100), deadline.At); // 1000 / 10 = 100s.

        // No Drill → deposit rate 0 → not draining → no deadline.
        var idle = CreateColonizedPlanet();
        Place(idle, _base, BuildingType.Generator);
        Assert.Null(idle.PredictDepletionDeadline(_base));
    }
}
