using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

// The finite ore deposit (#70) drains as drills extract, modeled as a ResourcePool so it inherits
// the #44 floored-elapsed/non-regressing invariant. Task 1 only drains it — no depletion halting.
public sealed class PlanetDepositTests
{
    // Fixed base time so drain math is deterministic (no DateTimeOffset.UtcNow).
    private static readonly DateTimeOffset _base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Seeded deposit from PlanetCreated below: 50000 ore, StorageCapacity 50000.
    private const decimal _initialDeposit = 50000m;

    private static Planet CreateColonizedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(Guid.NewGuid(), 500, 100, _base));
        return planet;
    }

    // A powered Drill sets the deposit draining at exactly -oreInflow, and the remaining pool
    // falls by oreInflow * elapsed, flooring at 0 once the deposit is fully exhausted.
    [Fact]
    public void OperationalDrillDrainsDepositAtExtractionRateFlooringAtZero()
    {
        var planet = CreateColonizedPlanet();

        // Generator (100 MW) powers the Drill (20 MW) at full rate → m = 1, oreInflow = 10/s.
        planet.Apply(new BuildingPlaced(BuildingType.Generator, _base));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, _base));

        var oreInflow = BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill); // m = 1.
        Assert.Equal(-oreInflow, planet.IronOreDeposit.Rate);

        // Checkpointed at _base with the full pool, so GetCurrentValue drains by oreInflow * N:
        // after 100s the deposit has lost 1000 ore.
        var drainedAt = _base.AddSeconds(100);
        Assert.Equal(_initialDeposit - (oreInflow * 100m), planet.IronOreDeposit.GetCurrentValue(drainedAt));

        // The pool empties after _initialDeposit / oreInflow = 5000s, then floors at 0 — never
        // negative, however long past exhaustion we read.
        var fullDrainSeconds = (double)(_initialDeposit / oreInflow);
        Assert.Equal(0m, planet.IronOreDeposit.GetCurrentValue(_base.AddSeconds(fullDrainSeconds)));
        Assert.Equal(0m, planet.IronOreDeposit.GetCurrentValue(_base.AddSeconds(fullDrainSeconds + 3600)));
    }

    // With no powered drill there is no extraction, so the deposit rate is 0 and the pool holds.
    [Fact]
    public void DepositDoesNotDrainWithoutAnOperationalDrill()
    {
        var planet = CreateColonizedPlanet();

        // A Drill with no Generator is energy-throttled to 0 inflow (m = 0), so nothing is extracted.
        planet.Apply(new BuildingPlaced(BuildingType.Drill, _base));

        Assert.Equal(0m, planet.IronOreDeposit.Rate);
        Assert.Equal(_initialDeposit, planet.IronOreDeposit.GetCurrentValue(_base.AddSeconds(3600)));
    }
}
