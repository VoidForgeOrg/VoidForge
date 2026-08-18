using Microsoft.Extensions.Configuration;
using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Domain;

[Trait("Category", "Unit")]
public sealed class EconomyRatesTests
{
    // The whole "boot-time static rate table" design rests on EconomyRates defaults being IDENTICAL to
    // the former hardcoded BuildingSpecs constants: that is what lets every pure-domain test read the
    // same rates without booting a host. If a default drifts here, this test (and the domain suite)
    // fail — which is the intended signal, not something to paper over.
    [Fact]
    public void DefaultsMatchLegacyBuildingSpecsConstants()
    {
        var rates = new EconomyRates();

        Assert.Equal(10m, rates.DrillOreRatePerSecond);
        Assert.Equal(5m, rates.RefineryOreConsumptionPerSecond);
        Assert.Equal(2m, rates.RefineryIngotOutputFactor);
        Assert.Equal(100m, rates.GeneratorEnergyOutputMw);
        Assert.Equal(20m, rates.DrillEnergyDrawMw);
        Assert.Equal(30m, rates.RefineryEnergyDrawMw);
        Assert.Equal(40m, rates.ShipyardEnergyDrawMw);
        Assert.Equal(0.05m, rates.HaltedDrawFactor);
        Assert.Equal(0.05m, rates.ShipyardIdleDrawFactor);
        Assert.Equal(3, rates.ShipyardParallelBuilds);
    }

    // Proves the "Economy" configuration section binds onto EconomyRates (the surface Program wires and
    // the verifier profile drives), overriding named leaves while absent leaves keep their defaults.
    [Fact]
    public void BindsFromEconomyConfigSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Economy:DrillOreRatePerSecond"] = "42",
                ["Economy:RefineryIngotOutputFactor"] = "3",
                ["Economy:ShipyardParallelBuilds"] = "5",
            })
            .Build();

        var rates = new EconomyRates();
        config.GetSection("Economy").Bind(rates);

        Assert.Equal(42m, rates.DrillOreRatePerSecond);
        Assert.Equal(3m, rates.RefineryIngotOutputFactor);
        Assert.Equal(5, rates.ShipyardParallelBuilds);
        Assert.Equal(30m, rates.RefineryEnergyDrawMw); // absent key keeps the default
    }
}
