namespace Voidforge.SoakTests;

// Whether one Tier-2 metric landed inside its tolerance band. Advisory only — NONE of these is an xUnit
// failure (§2/§7.3): WithinBand and Warn are the two comparison outcomes a human reviews, and Unresolved
// records a baseline/aggregate GAP (a metric absent from the run or the baseline, an unknown tolerance
// kind, or an undefined named tolerance) as data — surfaced instead of silently dropping the row or
// collapsing its band to a spurious Warn. A miss is still never a test failure, only a WARN a human reads.
public enum Tier2Status
{
    WithinBand,
    Warn,
    Unresolved,
}
