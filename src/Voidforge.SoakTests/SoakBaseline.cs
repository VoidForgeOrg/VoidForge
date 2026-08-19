namespace Voidforge.SoakTests;

// The deserialized blessed baseline (baselines/soak-baseline.json). Keyed by ScenarioId + the embedded
// Config block + WindowSeconds (§3.3): a baseline is only valid for the exact theme and window it was
// blessed against. Tolerances are named bands ("scorePct", "countAbs", "oreEpsilon") referenced by each
// metric's Tol. Dictionaries keep the schema open so adding a metric never touches this type.
public sealed record SoakBaseline(
    string ScenarioId,
    int WindowSeconds,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyDictionary<string, decimal> Tolerances,
    IReadOnlyDictionary<string, SoakBaselineMetric> Expected);
