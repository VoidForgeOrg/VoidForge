namespace Voidforge.SoakTests;

// Whether one Tier-2 metric landed inside its tolerance band. TWO states only — Tier 2 is advisory, so
// there is no hard-fail state: a miss is a WARN a human reviews, never an xUnit failure (§2/§7.3).
public enum Tier2Status
{
    WithinBand,
    Warn,
}
