using Xunit;
using Xunit.Abstractions;

namespace Voidforge.SoakTests;

// The input-starvation soak: a single-player world whose seeded Refinery is driven to an InputStarved
// halt (deposit empties -> Drill ResourceDepleted -> ore store drains -> Refinery InputStarved), while the
// ingot store keeps headroom so the storage-full path never fires. Proves the SoakScenario seam: a whole
// new, differently-shaped scenario is one SoakScenario + one fixture + this 3-line class.
// The scenario (theme + body + intent) is SoakScenarios.InputStarvation. No Tier-2 baseline — Tier 1 +
// Tier 3 (InputStarved observed) gate it. Deliberately out of the slnx — invoke manually.
[Trait("Category", "Soak")]
[Collection(InputStarvationCollection.Name)]
public sealed class InputStarvationSoakTests(InputStarvationFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public Task RefineryStarvesForOreInputAndHaltsCleanly() => SoakRunner.RunAsync(fixture, output);
}
