namespace Voidforge.SoakTests;

// The input-starvation scenario on its own DB (so it can run as a concurrent process alongside the
// two-user economy scenario — see scripts/soak-matrix.sh).
public sealed class InputStarvationFixture : SoakHostFixture
{
    protected override SoakScenario Scenario => SoakScenarios.InputStarvation;
}
