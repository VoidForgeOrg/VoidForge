namespace Voidforge.SoakTests;

// One metric's Tier-2 comparison: the observed run value against the blessed expected value within its
// tolerance band. Carries numbers + enums only (no pre-formatted strings) so all culture-sensitive
// rendering stays in the SoakReport.Emit invariant-culture choke point.
public sealed record Tier2Result(
    string Id,
    decimal Observed,
    decimal Expected,
    decimal Tolerance,
    Tier2ToleranceKind Kind,
    Tier2Status Status);
