using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace Voidforge.SoakTests;

// The soak run: boot the real host with a rich-economy config, drive two contending users over real
// HTTP for a bounded window (letting the REAL Wolverine scheduler fire completions), drain the
// scheduler, snapshot world state via Marten, then assert BOTH Tier-1 invariants I1-I11 (properties
// that hold for any legal state) AND Tier-3 structural outcomes O1-O6 (the scripted story actually
// happened — colonize, ship production, ore mined, transport delivery, depletion, halt cascades).
// The cascade-dependent Tier-3 outcomes (O4-O6) fire only at SOAK_WINDOW_SECONDS>=300; below that they
// are Skipped, so the default 120s run asserts O1-O3 and reports O4-O6 as SKIP.
// Deliberately out of the slnx, so no CI lane or Stop-hook runs it — invoke manually.
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
        // RunAsync now drains the scheduler internally (with the snapshot loop still capturing halts),
        // so the world is quiesced by the time it returns and drain-phase halts reach Tier 3's O6.
        var driverResult = await driver.RunAsync(TimeSpan.FromSeconds(SoakConfig.WindowSeconds), _output.WriteLine);

        var now = TimeProvider.System.GetUtcNow();
        var snapshot = await SoakSnapshotReader.ReadAuthoritativeAsync(
            store, now, SoakConfig.ConnectionString, driverResult.HttpStatuses, driverResult.DepositSeries);

        var balance = host.Services.GetRequiredService<IOptions<BalanceOptions>>().Value;
        Func<ShipType, decimal> capacityOf = t => balance.Ships.For(t).CargoCapacity;
        var scoreCalculator = host.Services.GetRequiredService<ScoreCalculator>();

        var tier1 = Tier1Invariants.Evaluate(snapshot, capacityOf, _overdueMargin);
        var tier3 = Tier3Outcomes.Evaluate(snapshot, ScenarioIntent.Default, SoakConfig.WindowSeconds);

        // Tier 2 is advisory: compute the run aggregates and compare them to the blessed baseline (skipped
        // when none is committed or the window does not match). It NEVER asserts — only Tier 1 and Tier 3
        // hard-fail the run.
        var aggregates = SoakAggregates.Compute(snapshot, scoreCalculator);
        var tier2 = Tier2Baseline.EvaluateOrSkip(aggregates, ScenarioIntent.ScenarioId, SoakConfig.WindowSeconds);

        // Render the full report BEFORE asserting, so the per-tier matrices reach the test output even
        // when an assertion below fails the run.
        _output.WriteLine(SoakReport.Render(snapshot, tier1, tier2, tier3, scoreCalculator, driverResult.Events));

        // Emit mode: log the machine-readable aggregates for the N-run blessing envelope (§6).
        if (SoakConfig.EmitBaseline)
        {
            SoakBaselineEmitter.Emit(aggregates, SoakConfig.WindowSeconds, _output.WriteLine);
        }

        Tier1Invariants.AssertAll(tier1);
        Tier3Outcomes.AssertAll(tier3);
    }
}
