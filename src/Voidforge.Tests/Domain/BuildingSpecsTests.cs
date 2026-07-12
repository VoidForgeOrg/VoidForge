using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Domain;

public sealed class BuildingSpecsTests
{
    [Fact]
    public void DrillExtractsIronOreAtPositiveRate()
    {
        Assert.Equal(10m, BuildingSpecs.IronOreRatePerSecond(BuildingType.Drill));
    }

    [Theory]
    [InlineData(BuildingType.Refinery)]
    [InlineData(BuildingType.Shipyard)]
    [InlineData(BuildingType.Generator)]
    public void NonDrillBuildingsExtractNoIronOre(BuildingType type)
    {
        Assert.Equal(0m, BuildingSpecs.IronOreRatePerSecond(type));
    }

    [Fact]
    public void GeneratorOutputsEnergy()
    {
        Assert.Equal(100m, BuildingSpecs.EnergyOutputMw(BuildingType.Generator));
    }

    [Theory]
    [InlineData(BuildingType.Drill)]
    [InlineData(BuildingType.Refinery)]
    [InlineData(BuildingType.Shipyard)]
    public void NonGeneratorBuildingsOutputNoEnergy(BuildingType type)
    {
        Assert.Equal(0m, BuildingSpecs.EnergyOutputMw(type));
    }

    [Theory]
    [InlineData(BuildingType.Drill, 20)]
    [InlineData(BuildingType.Refinery, 30)]
    [InlineData(BuildingType.Shipyard, 40)]
    public void EnergyConsumersDrawEnergy(BuildingType type, int expectedMw)
    {
        Assert.Equal(expectedMw, BuildingSpecs.EnergyDrawMw(type));
    }

    [Fact]
    public void GeneratorDrawsNoEnergy()
    {
        Assert.Equal(0m, BuildingSpecs.EnergyDrawMw(BuildingType.Generator));
    }

    [Fact]
    public void RefineryConsumesIronOreAtPositiveRate()
    {
        Assert.Equal(5m, BuildingSpecs.RefineryOreConsumptionPerSecond(BuildingType.Refinery));
    }

    [Theory]
    [InlineData(BuildingType.Drill)]
    [InlineData(BuildingType.Shipyard)]
    [InlineData(BuildingType.Generator)]
    public void NonRefineryBuildingsConsumeNoIronOre(BuildingType type)
    {
        Assert.Equal(0m, BuildingSpecs.RefineryOreConsumptionPerSecond(type));
    }

    [Fact]
    public void RefineryIngotOutputFactorIsTwo()
    {
        Assert.Equal(2m, BuildingSpecs.RefineryIngotOutputFactor);
    }

    [Fact]
    public void ShipyardParallelBuildsIsThree()
    {
        Assert.Equal(3, BuildingSpecs.ShipyardParallelBuilds);
    }

    [Fact]
    public void ShipyardIdleDrawFactorIsFivePercent()
    {
        Assert.Equal(0.05m, BuildingSpecs.ShipyardIdleDrawFactor);
    }
}
