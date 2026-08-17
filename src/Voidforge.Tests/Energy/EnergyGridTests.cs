using Alba;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Energy;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class EnergyGridTests
{
    private readonly IAlbaHost _host;

    public EnergyGridTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task HomeworldEnergyBlockReflectsStartingBuildings()
    {
        var registration = await _host.RegisterPlayer("Energy_Test_");

        var planet = await _host.GetPlanet(registration);

        // Starting composition: Generator (100 MW) vs Drill (20) + Refinery (30).
        Assert.Equal(100m, planet.Energy.GenerationMw);
        Assert.Equal(50m, planet.Energy.ConsumptionMw);
        Assert.Equal(1m, planet.Energy.ProductivityMultiplier);
    }

    // Overload throttling of the multiplier and of pool rates is covered by unit tests
    // (PlanetEnergyTests.OverloadScalesProductivityProportionally and
    // PlanetAggregateTests.ApplyBuildingPlacedOverloadRescalesExistingDrillRates). Since #26,
    // endpoint placement starts construction (UnderConstruction, no immediate operational
    // effect), so the former place-and-assert-overload integration tests were retired here;
    // the construction→operational→effects path is exercised end-to-end in
    // BuildingConstructionCompletionTests.

    [Fact]
    public async Task HomeworldRefineryProducesIngotsAtTwiceOreConsumption()
    {
        var registration = await _host.RegisterPlayer("Energy_Test_");

        var first = await _host.GetPlanet(registration);
        // Homeworld: drill inflow 10, refinery demand 5, m=1 => net ore +5/s, ingots +10/s.
        Assert.Equal(5m, first.IronOre.Rate);
        Assert.Equal(10m, first.IronIngot.Rate);

        await Task.Delay(1000);
        var second = await _host.GetPlanet(registration);

        // Both pools rise: here drill inflow (10) exceeds refinery demand (5), so the buffer is never
        // drawn down (#70 buffer-drain only kicks in when demand outstrips inflow) and net ore stays
        // positive; ingots climb at twice the effective ore consumption.
        Assert.True(second.IronIngot.CurrentValue > first.IronIngot.CurrentValue,
            $"Expected ingots to rise: {first.IronIngot.CurrentValue} -> {second.IronIngot.CurrentValue}");
        Assert.True(second.IronOre.CurrentValue > first.IronOre.CurrentValue,
            $"Expected net ore to rise: {first.IronOre.CurrentValue} -> {second.IronOre.CurrentValue}");
    }
}
