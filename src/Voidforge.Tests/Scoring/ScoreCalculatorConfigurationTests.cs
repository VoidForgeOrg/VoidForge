using Microsoft.Extensions.Options;
using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;
using Xunit;

namespace Voidforge.Tests.Scoring;

[Trait("Category", "Unit")]
public sealed class ScoreCalculatorConfigurationTests
{
    private static ScoreComponents Components(
        int planets,
        IReadOnlyDictionary<BuildingType, int>? buildings = null,
        IReadOnlyDictionary<ShipType, int>? ships = null,
        decimal ore = 0m,
        decimal ingot = 0m)
        => new(
            planets,
            buildings ?? new Dictionary<BuildingType, int>(),
            ships ?? new Dictionary<ShipType, int>(),
            ore,
            ingot);

    // Scoring is DI-injected (not a global), so a calculator built with custom ScoringOptions scores by
    // those weights — this is how the verifier / balancing drives the "Scoring" config section.
    [Fact]
    public void CustomScoringOptionsDriveTheScore()
    {
        var options = new ScoringOptions
        {
            PointsPerPlanet = 1000m,
            DrillPoints = 7m,
            IronIngotPointsPerUnit = 5m,
        };
        var calculator = new ScoreCalculator(Options.Create(options));

        var score = calculator.Score(Components(
            planets: 2,
            buildings: new Dictionary<BuildingType, int> { [BuildingType.Drill] = 3 },
            ingot: 4m));

        // 2*1000 + 3*7 + 4*5
        Assert.Equal((2 * 1000m) + (3 * 7m) + (4 * 5m), score);
    }

    // The parameterless calculator (used across the existing suite) must score identically to the
    // ScoringSpecs defaults — guards the "defaults unchanged" invariant behind ScoringOptions.
    [Fact]
    public void ParameterlessCalculatorMatchesScoringSpecsDefaults()
    {
        var calculator = new ScoreCalculator();

        var score = calculator.Score(Components(
            planets: 1,
            buildings: new Dictionary<BuildingType, int> { [BuildingType.Shipyard] = 1 },
            ships: new Dictionary<ShipType, int> { [ShipType.ColonyShip] = 1 },
            ore: 10m,
            ingot: 5m));

        var expected =
            ScoringSpecs.PointsPerPlanet
            + ScoringSpecs.BuildingPoints(BuildingType.Shipyard)
            + ScoringSpecs.ShipPoints(ShipType.ColonyShip)
            + (10m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronOre))
            + (5m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronIngot));
        Assert.Equal(expected, score);
    }
}
