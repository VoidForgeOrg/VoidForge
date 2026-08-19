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

    // Loads the committed baseline and compares against it, or SKIPs. Every load failure — file absent,
    // unreadable, malformed/truncated JSON, a null deserialization, or a missing required block — becomes a
    // SKIP, never a throw: Tier 2 is advisory (§2/§7.3), so a bad baseline must not fail the xUnit run
    // before Tier 1/Tier 3 render. Compatibility is gated on ScenarioId + WindowSeconds (the Config block is
    // provenance only — see SoakBaseline); a mismatch on either SKIPs, mirroring Tier 3's window-gated SKIP.
    public static Tier2Report EvaluateOrSkip(SoakAggregates actual, string scenarioId, int windowSeconds, string? baselineFile)
    {
        if (baselineFile is null)
        {
            return Tier2Report.SkippedReport("scenario declares no Tier-2 baseline (Tier 1 + Tier 3 gate this run)");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "baselines", baselineFile);
        if (!File.Exists(path))
        {
            return Tier2Report.SkippedReport($"no baseline committed at {path} — run SOAK_EMIT_BASELINE=1 at 300s to bless");
        }

        SoakBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<SoakBaseline>(File.ReadAllText(path), _json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Tier2Report.SkippedReport($"baseline at {path} could not be read: {ex.Message}");
        }

        if (baseline is null || baseline.Tolerances is null || baseline.Expected is null)
        {
            return Tier2Report.SkippedReport($"baseline at {path} is missing required 'tolerances'/'expected' blocks");
        }

        if (!string.Equals(baseline.ScenarioId, scenarioId, StringComparison.Ordinal))
        {
            return Tier2Report.SkippedReport(
                $"baseline scenario '{baseline.ScenarioId}' != run scenario '{scenarioId}' (baselines are per theme)");
        }

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
        var expected = baseline.Expected ?? new Dictionary<string, SoakBaselineMetric>(StringComparer.Ordinal);
        var tolerances = baseline.Tolerances ?? new Dictionary<string, decimal>(StringComparer.Ordinal);
        var results = new List<Tier2Result>();
        foreach (var id in _metricOrder)
        {
            // Surface a gap as data rather than dropping the row: a silently-missing blessed metric would let
            // AllWithinBand read "clean" while a metric was never compared.
            if (!metrics.TryGetValue(id, out var observed) || !expected.TryGetValue(id, out var metric))
            {
                results.Add(Tier2Result.Unresolved(id, $"metric '{id}' missing from run aggregates or baseline"));
                continue;
            }

            if (!TryParseKind(metric.Kind, out var kind))
            {
                results.Add(Tier2Result.Unresolved(id, $"unknown tolerance kind '{metric.Kind}' in baseline"));
                continue;
            }

            // A missing named tolerance is a baseline typo, not a drift — never collapse the band to 0m (that
            // would manufacture a spurious Warn); record it as Unresolved instead.
            if (metric.Tol is null || !tolerances.TryGetValue(metric.Tol, out var tolerance))
            {
                results.Add(Tier2Result.Unresolved(id, $"tolerance '{metric.Tol}' not defined in baseline"));
                continue;
            }

            var status = WithinBand(observed, metric.Value, tolerance, kind) ? Tier2Status.WithinBand : Tier2Status.Warn;
            results.Add(new Tier2Result(id, observed, metric.Value, tolerance, kind, status));
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

    // Non-throwing: an unknown or absent kind in the baseline JSON becomes an Unresolved row (advisory
    // tier never throws), not an exception that would fail the whole soak run before Tier 1/Tier 3 render.
    private static bool TryParseKind(string? kind, out Tier2ToleranceKind result)
    {
        switch (kind)
        {
            case "exact-ish": result = Tier2ToleranceKind.ExactIsh; return true;
            case "count": result = Tier2ToleranceKind.Count; return true;
            case "count-min": result = Tier2ToleranceKind.CountMin; return true;
            case "scalar": result = Tier2ToleranceKind.Scalar; return true;
            default: result = default; return false;
        }
    }
}
