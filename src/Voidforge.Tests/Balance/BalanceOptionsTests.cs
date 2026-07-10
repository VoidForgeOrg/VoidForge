using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Balance;

public sealed class BalanceOptionsTests
{
    [Fact]
    public void DefaultsMatchSpecPlaceholders()
    {
        var options = new BalanceOptions();

        Assert.Equal(300m, options.ForBuilding(BuildingType.Drill).IngotCost);
        Assert.Equal(60m, options.ForBuilding(BuildingType.Drill).BuildDurationSeconds);
        Assert.Equal(450m, options.ForBuilding(BuildingType.Refinery).IngotCost);
        Assert.Equal(240m, options.ForBuilding(BuildingType.Generator).IngotCost);
        Assert.Equal(600m, options.ForBuilding(BuildingType.Shipyard).IngotCost);
    }

    [Fact]
    public void DrainPerSecondIsCostOverDuration()
    {
        var balance = new ConstructionBalance { IngotCost = 300m, BuildDurationSeconds = 60m };
        Assert.Equal(5m, balance.DrainPerSecond);
    }

    [Fact]
    public void DrainPerSecondIsZeroForZeroDuration()
    {
        var balance = new ConstructionBalance { IngotCost = 300m, BuildDurationSeconds = 0m };
        Assert.Equal(0m, balance.DrainPerSecond);
    }
}
