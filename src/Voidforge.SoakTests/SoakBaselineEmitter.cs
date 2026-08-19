using System.Text.Json;

namespace Voidforge.SoakTests;

// Emit mode (SOAK_EMIT_BASELINE=1): after a run computes its SoakAggregates, log ONE compact, grep-able
// marker line carrying the machine-readable metrics. Feeds the N-run blessing envelope (§6): run the 300s
// soak 5x, grep the marker, and fold the JSON objects into the baseline. Chosen over a file write so it is
// immune to dotnet test working-dir / shadow-copy uncertainty — surface it with
// `dotnet test -l "console;verbosity=detailed"`. System.Text.Json writes numbers culture-invariantly, and
// the marker line is built by plain concatenation (no interpolation), so no culture surface leaks in.
public static class SoakBaselineEmitter
{
    public const string Marker = "SOAK_BASELINE_EMIT";

    public static void Emit(SoakAggregates aggregates, string scenarioId, int windowSeconds, Action<string> log)
    {
        var payload = new
        {
            scenarioId,
            windowSeconds,
            metrics = aggregates.ToMetrics(),
        };
        log(Marker + " " + JsonSerializer.Serialize(payload));
    }
}
