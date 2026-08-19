namespace Voidforge.SoakTests;

// One metric's Tier-2 comparison: the observed run value against the blessed expected value within its
// tolerance band. Carries numbers + enums only (no pre-formatted strings) so all culture-sensitive
// rendering stays in the SoakReport.Emit invariant-culture choke point. Reason is set only for an
// Unresolved row (the human-readable gap); the numeric fields are then placeholders the renderer ignores.
public sealed record Tier2Result(
    string Id,
    decimal Observed,
    decimal Expected,
    decimal Tolerance,
    Tier2ToleranceKind Kind,
    Tier2Status Status,
    string? Reason = null)
{
    // A metric that could not be compared: absent from the run aggregates or the baseline, an unknown
    // tolerance kind, or an undefined named tolerance. Kept as data (never thrown) so the advisory tier
    // surfaces the gap in the matrix instead of dropping the row or collapsing its band. Kind is a
    // placeholder — the renderer branches on Status before reading it.
    public static Tier2Result Unresolved(string id, string reason) =>
        new(id, 0m, 0m, 0m, Tier2ToleranceKind.ExactIsh, Tier2Status.Unresolved, reason);
}
