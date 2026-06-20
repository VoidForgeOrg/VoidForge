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
}
