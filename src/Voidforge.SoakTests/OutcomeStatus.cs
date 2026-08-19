namespace Voidforge.SoakTests;

// Whether a Tier-3 structural outcome was met. Tri-state: Skipped is for window-gated outcomes that
// this run is too short to produce (the cascades need SOAK_WINDOW_SECONDS>=300) — a Skipped outcome is
// reported but never fails the run, so the default 120s soak stays green.
public enum OutcomeStatus
{
    Passed,
    Failed,
    Skipped,
}
