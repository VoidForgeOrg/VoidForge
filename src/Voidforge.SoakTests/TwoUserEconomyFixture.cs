namespace Voidforge.SoakTests;

// The two-user economy scenario on its own DB.
public sealed class TwoUserEconomyFixture : SoakHostFixture
{
    protected override SoakScenario Scenario => SoakScenarios.TwoUserEconomy;
}
