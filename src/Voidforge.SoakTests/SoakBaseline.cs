namespace Voidforge.SoakTests;

// The deserialized blessed baseline (baselines/soak-baseline.json). Runtime compatibility is gated on
// ScenarioId + WindowSeconds: Tier2Baseline.EvaluateOrSkip SKIPs a run whose scenario or window does not
// match the blessed one (mirroring Tier 3's window-gated O4-O6 SKIP). The embedded Config block travels
// with the baseline as PROVENANCE — the exact env theme it was blessed against, for human re-bless review
// (§3.3) — but is deliberately NOT machine-compared: reproducing and diffing the full env theme is an
// error-prone heavy lift unjustified for an advisory tier, and a config change that should move the numbers
// already surfaces as a Tier-2 WARN that §3.3 tells the reviewer to re-bless. Reference-typed members are
// nullable because System.Text.Json assigns null for any omitted JSON property; EvaluateOrSkip treats a
// missing required block as a SKIP so a malformed/truncated baseline never fails this advisory tier.
// Tolerances are named bands ("scorePct", "countAbs", "oreEpsilon") referenced by each metric's Tol.
// Dictionaries keep the schema open so adding a metric never touches this type.
public sealed record SoakBaseline(
    string? ScenarioId,
    int WindowSeconds,
    IReadOnlyDictionary<string, string>? Config,
    IReadOnlyDictionary<string, decimal>? Tolerances,
    IReadOnlyDictionary<string, SoakBaselineMetric>? Expected);
