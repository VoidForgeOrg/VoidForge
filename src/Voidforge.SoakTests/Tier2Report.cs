namespace Voidforge.SoakTests;

// The full Tier-2 baseline comparison. Kept as data (mirrors Tier1Report/Tier3Report) so the test can
// render a per-metric matrix without re-evaluating. Advisory ONLY: there is deliberately no AssertAll and
// no failing state — AllWithinBand is a human-review signal, not a gate. A non-null SkipReason means the
// comparison did not run (no baseline committed, or the run window does not match the baseline's window).
public sealed record Tier2Report(IReadOnlyList<Tier2Result> Results, string? SkipReason)
{
    public bool Skipped => SkipReason is not null;

    // A skipped report never ran a comparison, so it is NOT "all within band" (an empty Results would
    // otherwise make Enumerable.All vacuously true). An Unresolved row also fails this — only a real
    // WithinBand-for-every-metric run is a clean advisory signal.
    public bool AllWithinBand => !Skipped && Results.All(r => r.Status == Tier2Status.WithinBand);

    public string WarnSummary() =>
        string.Join(Environment.NewLine, Results.Where(r => r.Status == Tier2Status.Warn).Select(r => r.Id));

    public static Tier2Report SkippedReport(string reason) => new([], reason);
}
