using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Xunit;

namespace Voidforge.Tests.Balance;

[Trait("Category", "Unit")]
public sealed class BalanceOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(BalanceOptions options) =>
        new BalanceOptionsValidator().Validate(Options.DefaultName, options);

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.True(Validate(new BalanceOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveDemolitionDurationFails(int seconds)
    {
        // A non-positive demolition duration schedules the slot-freeing teardown at or before "now".
        var result = Validate(new BalanceOptions { DemolitionDurationSeconds = seconds });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("DemolitionDurationSeconds"));
    }

    [Fact]
    public void NonPositiveBuildDurationFails()
    {
        var options = new BalanceOptions
        {
            Drill = new ConstructionBalance { IngotCost = 300m, BuildDurationSeconds = 0m },
        };

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Drill:BuildDurationSeconds"));
    }

    [Fact]
    public void NegativeIngotCostFails()
    {
        var options = new BalanceOptions
        {
            Refinery = new ConstructionBalance { IngotCost = -1m, BuildDurationSeconds = 90m },
        };

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Refinery:IngotCost"));
    }

    [Fact]
    public void NonPositiveShipSpeedFails()
    {
        // Ship speed is a divisor when computing travel time (distance / speed).
        var options = new BalanceOptions
        {
            Ships = new ShipsBalanceOptions
            {
                CargoVessel = new ShipBalance { SpeedPerSecond = 0m, CargoCapacity = 500m },
            },
        };

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Ships:CargoVessel:SpeedPerSecond"));
    }
}
