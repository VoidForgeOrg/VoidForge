namespace Voidforge.SoakTests;

// The full Tier-3 structural-outcome evaluation. Kept as data (mirrors Tier1Report) so the test can
// BOTH print a per-outcome matrix and assert on the result without re-evaluating. A Skipped outcome
// never fails: AllRequiredPassed is true unless something actually Failed.
public sealed record Tier3Report(IReadOnlyList<OutcomeResult> Results)
{
    public bool AllRequiredPassed => Results.All(r => r.Status != OutcomeStatus.Failed);

    public string FailureSummary() =>
        string.Join(
            Environment.NewLine,
            Results.Where(r => r.Status == OutcomeStatus.Failed).Select(FormatFailure));

    private static string FormatFailure(OutcomeResult result) =>
        $"{result.Id} ({result.Title}) FAILED: {result.Detail}";
}
