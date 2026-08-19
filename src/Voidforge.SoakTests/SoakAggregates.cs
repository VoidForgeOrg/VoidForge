using Voidforge.Api.Domain;
using Voidforge.Api.Scoring;

namespace Voidforge.SoakTests;

// The low-jitter run aggregates Tier 2 compares against the blessed baseline, computed from the single
// authoritative post-drain WorldSnapshot at its fixed Now. This is the CANONICAL home for these formulas
// for Tier 2. The shipped Tier-3 hard gate still computes the same quantities inline (its versions are
// entangled with per-check detail strings) — the two MUST stay in sync until a CI net lets Tier 3
// converge onto this type (see verifier-tier2-handover.md). Per §2 the blessed metrics are counts + the
// exact monotonic ore total + one score scalar: the run-stable signals, never instantaneous buffer levels.
public sealed record SoakAggregates(
    decimal OreMinedTotal,
    int PlanetsColonized,
    int ShipsProduced,
    int HaltReasonsSeen,
    decimal PlayerScoreMax)
{
    public static SoakAggregates Compute(WorldSnapshot s, ScoreCalculator scoreCalculator)
    {
        // Exact & monotonic (I11): initial deposit (its StorageCapacity) minus what remains.
        var oreMinedTotal = s.Planets.Sum(p => p.IronOreDeposit.StorageCapacity - p.IronOreDeposit.GetCurrentValue(s.Now));

        // Registration claims exactly one homeworld per player, so owned planets beyond the player count
        // are colonies won (mirrors Tier3Outcomes.ColoniesWon).
        var planetsColonized = s.Planets.Count(p => p.OwnerId is not null) - s.Players.Count;

        // Ships evidenced across rosters + live (non-Disbanded) fleets + colonies won (a colonize consumes
        // a colony ship off its fleet, so colonies-won backfills the consumed ones) — mirrors O2.
        var rosterShips = s.Planets.Sum(p => p.Ships.Count);
        var fleetShips = s.Fleets.Where(f => f.Status != FleetStatus.Disbanded).Sum(f => f.Ships.Count);
        var shipsProduced = rosterShips + fleetShips + planetsColonized;

        var haltReasonsSeen = ObservedHaltReasons(s).Count;

        // Per-player score via the real ScoreCalculator, all pools evaluated at the single snapshot Now.
        // DefaultIfEmpty guards the (illegal-in-scenario but defensive) zero-players case so Max never throws.
        var playerScoreMax = s.Players
            .Select(player => scoreCalculator.Compute(
                s.Planets.Where(p => p.OwnerId == player.Id).ToList(),
                s.Fleets.Where(f => f.OwnerId == player.Id).ToList(),
                s.Now))
            .DefaultIfEmpty(0m)
            .Max();

        return new SoakAggregates(oreMinedTotal, planetsColonized, shipsProduced, haltReasonsSeen, playerScoreMax);
    }

    // Canonical metric-id -> value map: the SINGLE source of the metric ids, shared by the baseline
    // comparator (Tier2Baseline) and the emitter (SoakBaselineEmitter) so compared ids and emitted ids can
    // never drift. Concrete Dictionary built here (CA1859); exposed as read-only at the boundary.
    public IReadOnlyDictionary<string, decimal> ToMetrics()
    {
        var metrics = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["oreMinedTotal"] = OreMinedTotal,
            ["planetsColonized"] = PlanetsColonized,
            ["shipsProduced"] = ShipsProduced,
            ["haltReasonsSeen"] = HaltReasonsSeen,
            ["playerScoreMax"] = PlayerScoreMax,
        };
        return metrics;
    }

    // Distinct halt reasons observed anywhere during the run: the union of every captured intermediate
    // snapshot's live halts and the final snapshot's building halt reasons (mirrors Tier3Outcomes).
    private static HashSet<HaltReason> ObservedHaltReasons(WorldSnapshot s)
    {
        var reasons = new HashSet<HaltReason>();
        foreach (var snap in s.DepositSeries)
        {
            foreach (var halt in snap.Halts)
            {
                reasons.Add(halt.Reason);
            }
        }

        foreach (var p in s.Planets)
        {
            foreach (var b in p.Buildings)
            {
                if (b.HaltReason is { } reason)
                {
                    reasons.Add(reason);
                }
            }
        }

        return reasons;
    }
}
