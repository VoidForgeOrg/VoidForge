using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace Voidforge.SoakTests;

// The v1 walking-skeleton soak run: boot the real host with a rich-economy config, drive two
// contending users over real HTTP for a bounded window (letting the REAL Wolverine scheduler fire
// completions), drain the scheduler, snapshot world state via Marten, and assert Tier-1 invariants
// I1-I11. Deliberately out of the slnx, so no CI lane or Stop-hook runs it — invoke manually.
[Trait("Category", "Soak")]
[Collection(SoakCollection.Name)]
public sealed class TwoUserEconomySoakTests
{
    // Must match SchedulerQuiescence's overdue margin so I6 uses the same tolerance the drain did.
    private static readonly TimeSpan _overdueMargin = TimeSpan.FromSeconds(10);

    private readonly SoakHostFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TwoUserEconomySoakTests(SoakHostFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task TwoContendingUsersLeaveEveryTier1InvariantIntact()
    {
        var host = _fixture.Host;
        var store = _fixture.Store;

        var driver = new SoakDriver(host, store);
        var driverResult = await driver.RunAsync(TimeSpan.FromSeconds(SoakConfig.WindowSeconds), _output.WriteLine);

        await SchedulerQuiescence.DrainAsync(store, _output.WriteLine);

        var now = TimeProvider.System.GetUtcNow();
        var snapshot = await SoakSnapshotReader.ReadAuthoritativeAsync(
            store, now, SoakConfig.ConnectionString, driverResult.HttpStatuses, driverResult.DepositSeries);

        var balance = host.Services.GetRequiredService<IOptions<BalanceOptions>>().Value;
        Func<ShipType, decimal> capacityOf = t => balance.Ships.For(t).CargoCapacity;
        var scoreCalculator = host.Services.GetRequiredService<ScoreCalculator>();

        var report = Tier1Invariants.Evaluate(snapshot, capacityOf, _overdueMargin);
        _output.WriteLine(SoakReport.Render(snapshot, report, scoreCalculator, driverResult.Events));

        Tier1Invariants.AssertAll(report);
    }
}
