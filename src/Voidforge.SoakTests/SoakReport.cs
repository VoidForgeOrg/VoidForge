using System.Globalization;
using System.Text;
using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;

namespace Voidforge.SoakTests;

// A compact human-readable console report: the per-invariant PASS/FAIL matrix plus raw aggregates
// (ore mined per planet, asset counts, per-player score). The seed for a future Tier-2 blessing.
public static class SoakReport
{
    public static string Render(
        WorldSnapshot s, Tier1Report tier1, Tier2Report tier2, Tier3Report tier3, ScoreCalculator scoreCalculator, IReadOnlyList<string> legEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Voidforge Soak Report (Tier 1 + Tier 2 + Tier 3) ===");
        Emit(sb, $"Snapshot instant : {s.Now}");
        Emit(sb, $"Planets {s.Planets.Count}  Fleets {s.Fleets.Count}  Players {s.Players.Count}");
        Emit(sb, $"Dead letters     : {s.DeadLetterCount}");
        Emit(sb, $"Raced statuses   : {FormatStatuses(s.HttpStatuses)}");
        Emit(sb, $"Deposit snapshots: {s.DepositSeries.Count}");
        sb.AppendLine();
        AppendInvariants(sb, tier1);
        sb.AppendLine();
        AppendOutcomes(sb, tier3);
        sb.AppendLine();
        AppendBaseline(sb, tier2);
        sb.AppendLine();
        AppendOreMined(sb, s);
        sb.AppendLine();
        AppendScores(sb, s, scoreCalculator);
        sb.AppendLine();
        AppendLegEvents(sb, legEvents);
        return sb.ToString();
    }

    private static string FormatStatuses(IReadOnlyList<int> statuses)
    {
        if (statuses.Count == 0)
        {
            return "none";
        }

        var grouped = statuses
            .GroupBy(c => c)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}x{g.Count()}");
        return string.Join(", ", grouped);
    }

    private static void AppendInvariants(StringBuilder sb, Tier1Report report)
    {
        sb.AppendLine("Invariants:");
        foreach (var result in report.Results)
        {
            Emit(sb, $"  [{(result.Passed ? "PASS" : "FAIL")}] {result.Id} {result.Title}");
            foreach (var violation in result.Violations)
            {
                Emit(sb, $"         - {violation}");
            }
        }
    }

    private static void AppendOutcomes(StringBuilder sb, Tier3Report report)
    {
        sb.AppendLine("Structural outcomes (Tier 3):");
        foreach (var result in report.Results)
        {
            var tag = result.Status switch
            {
                OutcomeStatus.Passed => "PASS",
                OutcomeStatus.Failed => "FAIL",
                _ => "SKIP",
            };
            Emit(sb, $"  [{tag}] {result.Id} {result.Title} - {result.Detail}");
        }
    }

    // Tier-2 baseline matrix: [BAND]/[WARN] per metric, or a single [SKIP] line when no baseline is
    // committed / the window does not match. Advisory only — a [WARN] never fails the run.
    private static void AppendBaseline(StringBuilder sb, Tier2Report report)
    {
        sb.AppendLine("Baseline comparison (Tier 2, advisory):");
        if (report.Skipped)
        {
            Emit(sb, $"  [SKIP] {report.SkipReason}");
            return;
        }

        foreach (var r in report.Results)
        {
            var tag = r.Status == Tier2Status.WithinBand ? "BAND" : "WARN";
            Emit(sb, $"  [{tag}] {r.Id}: observed {r.Observed} vs expected {r.Expected} {FormatBand(r)}");
        }
    }

    private static string FormatBand(Tier2Result r) =>
        r.Kind switch
        {
            Tier2ToleranceKind.CountMin => FormattableString.Invariant($">={r.Expected}"),
            Tier2ToleranceKind.Scalar => FormattableString.Invariant($"±{r.Tolerance}%"),
            _ => FormattableString.Invariant($"±{r.Tolerance}"),
        };

    private static void AppendOreMined(StringBuilder sb, WorldSnapshot s)
    {
        sb.AppendLine("Ore mined (initial deposit - current), producing planets only:");
        var any = false;
        foreach (var p in s.Planets)
        {
            var initial = p.IronOreDeposit.StorageCapacity;
            var current = p.IronOreDeposit.GetCurrentValue(s.Now);
            var mined = initial - current;
            if (mined > 0m)
            {
                any = true;
                Emit(sb, $"  planet {p.Id}: {mined} mined ({current}/{initial} remaining)");
            }
        }

        if (!any)
        {
            sb.AppendLine("  (none)");
        }
    }

    private static void AppendScores(StringBuilder sb, WorldSnapshot s, ScoreCalculator scoreCalculator)
    {
        sb.AppendLine("Per-player score:");
        foreach (var player in s.Players)
        {
            var ownedPlanets = s.Planets.Where(p => p.OwnerId == player.Id).ToList();
            var ownedFleets = s.Fleets.Where(f => f.OwnerId == player.Id).ToList();
            var score = scoreCalculator.Compute(ownedPlanets, ownedFleets, s.Now);
            Emit(sb, $"  {player.Name}: score {score} ({ownedPlanets.Count} planet(s), {ownedFleets.Count} fleet(s))");
        }
    }

    private static void AppendLegEvents(StringBuilder sb, IReadOnlyList<string> legEvents)
    {
        sb.AppendLine("Driver leg events:");
        foreach (var evt in legEvents)
        {
            Emit(sb, $"  {evt}");
        }
    }

    // Appends an interpolated line under the invariant culture — avoids CA1305/MA0011 on the
    // interpolated StringBuilder.AppendLine overload while keeping the call sites readable.
    private static void Emit(StringBuilder sb, FormattableString line) =>
        sb.AppendLine(line.ToString(CultureInfo.InvariantCulture));
}
