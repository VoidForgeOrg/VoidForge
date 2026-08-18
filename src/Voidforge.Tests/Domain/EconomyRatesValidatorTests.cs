using Microsoft.Extensions.Options;
using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Domain;

[Trait("Category", "Unit")]
public sealed class EconomyRatesValidatorTests
{
    private static ValidateOptionsResult Validate(EconomyRates rates) =>
        new EconomyRatesValidator().Validate(Options.DefaultName, rates);

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.True(Validate(new EconomyRates()).Succeeded);
    }

    [Fact]
    public void NegativeRateFails()
    {
        var result = Validate(new EconomyRates { DrillOreRatePerSecond = -1m });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("DrillOreRatePerSecond"));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void DrawFactorOutsideUnitIntervalFails(double factor)
    {
        var result = Validate(new EconomyRates { HaltedDrawFactor = (decimal)factor });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("HaltedDrawFactor"));
    }

    [Fact]
    public void ShipyardIdleDrawFactorAboveOneFails()
    {
        var result = Validate(new EconomyRates { ShipyardIdleDrawFactor = 2m });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ShipyardIdleDrawFactor"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveShipyardParallelBuildsFails(int value)
    {
        // ShipyardParallelBuilds is a divisor in Planet.Energy — zero or negative must be rejected.
        var result = Validate(new EconomyRates { ShipyardParallelBuilds = value });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ShipyardParallelBuilds"));
    }

    [Fact]
    public void MultipleViolationsAreAllReported()
    {
        var result = Validate(new EconomyRates
        {
            GeneratorEnergyOutputMw = -100m,
            HaltedDrawFactor = 5m,
            ShipyardParallelBuilds = 0,
        });

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }
}
