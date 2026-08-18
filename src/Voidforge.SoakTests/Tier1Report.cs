namespace Voidforge.SoakTests;

// The full I1–I11 evaluation. Kept as data so the test can BOTH print a per-invariant matrix and
// assert on the outcome without re-evaluating.
public sealed record Tier1Report(IReadOnlyList<InvariantResult> Results)
{
    public bool AllPassed => Results.All(r => r.Passed);

    public string FailureSummary() =>
        string.Join(
            Environment.NewLine,
            Results.Where(r => !r.Passed).Select(FormatFailure));

    private static string FormatFailure(InvariantResult result) =>
        $"{result.Id} ({result.Title}) FAILED:" + Environment.NewLine + "  " +
        string.Join(Environment.NewLine + "  ", result.Violations);
}
