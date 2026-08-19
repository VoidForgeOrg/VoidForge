using System.Text.Json;

namespace Voidforge.SoakTests;

// Loads the committed blessed baseline and compares a run's SoakAggregates against it, producing a
// render-only Tier2Report. NEVER asserts — Tier 2 is advisory (a miss is a WARN, not a test failure;
// §2/§7.3). Skips (rather than fails) when no baseline is committed or when the run window does not match
// the baseline's, exactly mirroring Tier 3's window-gated O4-O6 SKIP.
public static class Tier2Baseline
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Canonical comparison order, tightest -> jitteriest (§5). A fixed array drives iteration so the matrix
    // is deterministic and we never order-by strings or rely on dictionary enumeration order (MA0002).
    private static readonly string[] _metricOrder =
    [
        "oreMinedTotal",
        "planetsColonized",
        "shipsProduced",
        "haltReasonsSeen",
        "playerScoreMax",
    ];

    public static Tier2Report EvaluateOrSkip(SoakAggregates actual, int windowSeconds)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "baselines", "soak-baseline.json");
        if (!File.Exists(path))
        {
            return Tier2Report.SkippedReport("no baseline committed — run SOAK_EMIT_BASELINE=1 at 300s to bless");
        }

        var baseline = JsonSerializer.Deserialize<SoakBaseline>(File.ReadAllText(path), _json)
            ?? throw new InvalidOperationException($"Baseline at {path} deserialized to null.");

        if (baseline.WindowSeconds != windowSeconds)
        {
            return Tier2Report.SkippedReport(
                $"baseline window {baseline.WindowSeconds}s != run window {windowSeconds}s (bless & compare at 300s)");
        }

        return Evaluate(actual, baseline);
    }

    public static Tier2Report Evaluate(SoakAggregates actual, SoakBaseline baseline)
    {
        var metrics = actual.ToMetrics();
        var results = new List<Tier2Result>();
        foreach (var id in _metricOrder)
        {
            if (!metrics.TryGetValue(id, out var observed) ||
                !baseline.Expected.TryGetValue(id, out var expected))
            {
                continue;
            }

            var kind = ParseKind(expected.Kind);
            var tolerance = baseline.Tolerances.TryGetValue(expected.Tol, out var t) ? t : 0m;
            var status = WithinBand(observed, expected.Value, tolerance, kind) ? Tier2Status.WithinBand : Tier2Status.Warn;
            results.Add(new Tier2Result(id, observed, expected.Value, tolerance, kind, status));
        }

        return new Tier2Report(results, SkipReason: null);
    }

    private static bool WithinBand(decimal observed, decimal expected, decimal tolerance, Tier2ToleranceKind kind) =>
        kind switch
        {
            Tier2ToleranceKind.ExactIsh => Math.Abs(observed - expected) <= tolerance,
            Tier2ToleranceKind.Count => Math.Abs(observed - expected) <= tolerance,
            Tier2ToleranceKind.CountMin => observed >= expected,
            Tier2ToleranceKind.Scalar => Math.Abs(observed - expected) <= expected * (tolerance / 100m),
            _ => throw new InvalidOperationException($"Unhandled Tier-2 tolerance kind '{kind}'."),
        };

    private static Tier2ToleranceKind ParseKind(string kind) =>
        kind switch
        {
            "exact-ish" => Tier2ToleranceKind.ExactIsh,
            "count" => Tier2ToleranceKind.Count,
            "count-min" => Tier2ToleranceKind.CountMin,
            "scalar" => Tier2ToleranceKind.Scalar,
            _ => throw new InvalidOperationException($"Unknown Tier-2 tolerance kind '{kind}' in baseline JSON."),
        };
}
