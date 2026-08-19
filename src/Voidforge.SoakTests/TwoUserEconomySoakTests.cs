using Xunit;
using Xunit.Abstractions;

namespace Voidforge.SoakTests;

// The two-user economy soak: two contending users over real HTTP, real Wolverine scheduler, real
// optimistic concurrency. Boots + drives + drains + snapshots + asserts all three tiers via SoakRunner;
// the scenario (theme + scripts + intent + baseline) is SoakScenarios.TwoUserEconomy.
// Deliberately out of the slnx, so no CI lane or Stop-hook runs it — invoke manually.
[Trait("Category", "Soak")]
[Collection(TwoUserEconomyCollection.Name)]
public sealed class TwoUserEconomySoakTests(TwoUserEconomyFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public Task TwoContendingUsersLeaveEveryTier1InvariantIntact() => SoakRunner.RunAsync(fixture, output);
}
