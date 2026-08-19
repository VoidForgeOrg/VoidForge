using Voidforge.Api.Domain;

namespace Voidforge.SoakTests;

// The scenario's DECLARED INTENT as data: the thresholds Tier 3 asserts the scripted story reached,
// WHICH cascade-dependent outcomes apply to this scenario, and the window below which those outcomes are
// Skipped rather than required. Centralised here so a calibration pass touches ONE place.
//
// - MinColoniesWon:    colonies owned beyond the registered homeworlds (0 for a scenario that never colonizes).
// - MinShipsProduced:  ships evidenced across rosters + live fleets + colonies won.
// - MinOreMined:       total ore extracted so far, Sigma(initialDeposit - currentDeposit); proves the
//                      economy actually ran rather than idling.
// - CascadeWindowSeconds: below this window the cascade-dependent outcomes (O4-O6) genuinely cannot fire,
//                      so they are Skipped, not Failed.
// - ExpectsTransport:  gates O4 (a delivered supply run). False for a scenario with no transport.
// - ExpectsDepletion:  gates O5 (a deposit observed going positive -> 0).
// - ExpectedHalts:     drives O6 — the halt reason(s) this scenario is written to produce (O6 asserts every
//                      listed reason was observed; extra reasons are fine). Empty => O6 Skipped.
public sealed record ScenarioIntent(
    int MinColoniesWon,
    int MinShipsProduced,
    decimal MinOreMined,
    int CascadeWindowSeconds,
    bool ExpectsTransport,
    bool ExpectsDepletion,
    IReadOnlyList<HaltReason> ExpectedHalts)
{
    // The two-user "own-colony supply line + depletion" scenario's intent. Thresholds are intentionally
    // conservative (Tier 3 must carry slack — it flags "the story didn't happen", never contention jitter).
    // Unchanged from the pre-seam behavior: transport + depletion expected, both halt reasons required.
    public static ScenarioIntent Default { get; } = new(
        MinColoniesWon: 1,
        MinShipsProduced: 2,
        MinOreMined: 100m,
        CascadeWindowSeconds: 300,
        ExpectsTransport: true,
        ExpectsDepletion: true,
        ExpectedHalts: [HaltReason.ResourceDepleted, HaltReason.OutputStorageFull]);

    public bool CascadesExpected(int windowSeconds) => windowSeconds >= CascadeWindowSeconds;
}
