using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;
using Xunit.Abstractions;

namespace Voidforge.SoakTests;

// The scenario-agnostic soak run: boot the real host (via the fixture), drive the scenario body over real
// HTTP for a bounded window while the REAL Wolverine scheduler fires completions, drain the scheduler,
// take one authoritative Marten snapshot, then evaluate + assert all three tiers. Tier 1 and Tier 3 hard
// gate; Tier 2 is advisory (a WARN never fails the run). Every per-scenario test class is a thin wrapper
// over this — the scenario supplies its Id / Intent / BaselineFile through the fixture.
public static class SoakRunner
{
    // Must match SchedulerQuiescence's overdue margin so I6 uses the same tolerance the drain did.
    private static readonly TimeSpan _overdueMargin = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(SoakHostFixture fixture, ITestOutputHelper output)
    {
        var scenario = fixture.ActiveScenario;
        var host = fixture.Host;
        var store = fixture.Store;

        // RunAsync drains the scheduler internally (with the snapshot loop still capturing halts), so the
        // world is quiesced by the time it returns and drain-phase halts reach Tier 3's O6.
        var driver = new SoakDriver(host, store);
        var driverResult = await driver.RunAsync(scenario.Body, TimeSpan.FromSeconds(SoakConfig.WindowSeconds), output.WriteLine);

        var now = TimeProvider.System.GetUtcNow();
        var snapshot = await SoakSnapshotReader.ReadAuthoritativeAsync(
            store, now, SoakConfig.ConnectionStringFor(scenario.DbName), driverResult.HttpStatuses, driverResult.DepositSeries);

        var balance = host.Services.GetRequiredService<IOptions<BalanceOptions>>().Value;
        Func<ShipType, decimal> capacityOf = t => balance.Ships.For(t).CargoCapacity;
        var scoreCalculator = host.Services.GetRequiredService<ScoreCalculator>();

        var tier1 = Tier1Invariants.Evaluate(snapshot, capacityOf, _overdueMargin);
        var tier3 = Tier3Outcomes.Evaluate(snapshot, scenario.Intent, SoakConfig.WindowSeconds);

        // Tier 2 is advisory: compute the run aggregates and compare them to the scenario's blessed baseline
        // (skipped when the scenario declares none, none is committed, or the window does not match). It
        // NEVER asserts — only Tier 1 and Tier 3 hard-fail the run.
        var aggregates = SoakAggregates.Compute(snapshot, scoreCalculator);
        var tier2 = Tier2Baseline.EvaluateOrSkip(aggregates, scenario.Id, SoakConfig.WindowSeconds, scenario.BaselineFile);

        // Render the full report BEFORE asserting, so the per-tier matrices reach the test output even when
        // an assertion below fails the run.
        output.WriteLine(SoakReport.Render(snapshot, tier1, tier2, tier3, scoreCalculator, driverResult.Events));

        // Emit mode: log the machine-readable aggregates for the N-run blessing envelope.
        if (SoakConfig.EmitBaseline)
        {
            SoakBaselineEmitter.Emit(aggregates, scenario.Id, SoakConfig.WindowSeconds, output.WriteLine);
        }

        Tier1Invariants.AssertAll(tier1);
        Tier3Outcomes.AssertAll(tier3);
    }
}
