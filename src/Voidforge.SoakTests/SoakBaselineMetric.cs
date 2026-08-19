namespace Voidforge.SoakTests;

// One blessed metric in the baseline JSON: the expected value, its comparison kind ("exact-ish",
// "count", "count-min", "scalar") and the named tolerance it references in the baseline's "tolerances"
// block. Kind stays a string here (the JSON values contain hyphens System.Text.Json cannot map to enum
// names); Tier2Baseline maps it to Tier2ToleranceKind at compare time. Kind/Tol are nullable because
// System.Text.Json assigns null for an omitted property — Tier2Baseline records such a metric as an
// Unresolved row rather than throwing, keeping the tier advisory-only.
public sealed record SoakBaselineMetric(decimal Value, string? Kind, string? Tol);
