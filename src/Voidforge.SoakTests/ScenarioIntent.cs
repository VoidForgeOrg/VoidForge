namespace Voidforge.SoakTests;

// The scenario's DECLARED INTENT as data: the thresholds Tier 3 asserts the scripted story reached,
// plus the window below which the cascade-dependent outcomes (O4-O6) are Skipped rather than required.
// Centralised here so a calibration pass (see the plan's verification step) touches ONE place.
//
// - MinColoniesWon:    colonies owned beyond the two registered homeworlds (A + B each colonize a 2nd).
// - MinShipsProduced:  ships evidenced across rosters + live fleets + colonies won (each colonize
//                      consumed a colony ship, so colonies-won backfills the consumed ones).
// - MinOreMined:       total ore extracted so far, Sigma(initialDeposit - currentDeposit); proves the
//                      economy actually ran rather than idling.
// - CascadeWindowSeconds: the §8.2 depletion + ingot-storage-full cascades fire at ~170-200s, so a run
//                      shorter than this genuinely cannot produce O4-O6 — they are Skipped, not Failed.
public sealed record ScenarioIntent(
    int MinColoniesWon,
    int MinShipsProduced,
    decimal MinOreMined,
    int CascadeWindowSeconds)
{
    // The two-user "own-colony supply line + depletion" scenario's intent. Thresholds are intentionally
    // conservative (Tier 3 must carry slack — it flags "the story didn't happen", never contention jitter).
    public static ScenarioIntent Default { get; } = new(
        MinColoniesWon: 1,
        MinShipsProduced: 2,
        MinOreMined: 100m,
        CascadeWindowSeconds: 300);

    public bool CascadesExpected(int windowSeconds) => windowSeconds >= CascadeWindowSeconds;
}
