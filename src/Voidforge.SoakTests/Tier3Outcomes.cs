using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.SoakTests;

// Tier-3 STRUCTURAL OUTCOMES: "did the scripted story actually happen?" Where Tier 1 asserts invariants
// that hold for ANY legal state (and so pass vacuously on a near-empty world), Tier 3 asserts the
// existence/threshold facts the two-user scenario was WRITTEN to produce. Every check is derived purely
// from the drained WorldSnapshot + the intermediate series — never from the driver's own leg log — so a
// pass means the WORLD reflects the story, not that the driver believed it did.
//
// A key world-truth this relies on: a fleet-colonized planet starts BARE (Planet.Claim -> zero stores,
// no buildings), while a homeworld always carries its seeded buildings. So "an owned planet with zero
// live buildings" is unambiguously a colony, and any ore on such a planet can only have arrived by a
// delivered Transport.
public static class Tier3Outcomes
{
    public static Tier3Report Evaluate(WorldSnapshot s, ScenarioIntent intent, int windowSeconds)
    {
        var cascadesExpected = intent.CascadesExpected(windowSeconds);
        var results = new List<OutcomeResult>
        {
            CheckColonization(s, intent),
            CheckShipsProduced(s, intent),
            CheckOreMined(s, intent),
            CheckTransportDelivered(s, cascadesExpected),
            CheckDepletionFired(s, cascadesExpected),
            CheckHaltCascade(s, cascadesExpected),
        };
        return new Tier3Report(results);
    }

    public static void AssertAll(Tier3Report report) =>
        Assert.True(
            report.AllRequiredPassed,
            "Tier-3 structural outcome(s) not met:" + Environment.NewLine + report.FailureSummary());

    // O1: colonization happened — more planets are owned than the players' starting homeworlds (each
    // registered player claims exactly one homeworld, so owned - players = colonies won).
    private static OutcomeResult CheckColonization(WorldSnapshot s, ScenarioIntent intent)
    {
        var coloniesWon = ColoniesWon(s);
        return Threshold(
            "O1", "colonization occurred", coloniesWon >= intent.MinColoniesWon,
            $"{coloniesWon} colony(ies) won beyond {s.Players.Count} homeworld(s) (need >= {intent.MinColoniesWon})");
    }

    // O2: the shipyards actually produced ships — counted across rosters, live (non-Disbanded) fleets,
    // and colonies won (a successful colonize CONSUMES a colony ship off the fleet, so it no longer
    // shows on a roster/fleet; colonies-won backfills those).
    private static OutcomeResult CheckShipsProduced(WorldSnapshot s, ScenarioIntent intent)
    {
        var rosterShips = s.Planets.Sum(p => p.Ships.Count);
        var fleetShips = s.Fleets.Where(f => f.Status != FleetStatus.Disbanded).Sum(f => f.Ships.Count);
        var coloniesWon = ColoniesWon(s);
        var produced = rosterShips + fleetShips + coloniesWon;
        return Threshold(
            "O2", "ships produced", produced >= intent.MinShipsProduced,
            $"{produced} ship(s) produced (roster {rosterShips} + fleets {fleetShips} + colony-ships-consumed {coloniesWon}; need >= {intent.MinShipsProduced})");
    }

    // O3: the economy ran — total ore extracted so far (exact, monotonic: initial deposit - current).
    private static OutcomeResult CheckOreMined(WorldSnapshot s, ScenarioIntent intent)
    {
        var mined = s.Planets.Sum(p => p.IronOreDeposit.StorageCapacity - p.IronOreDeposit.GetCurrentValue(s.Now));
        return Threshold(
            "O3", "ore mined", mined >= intent.MinOreMined,
            $"{mined} ore mined across all deposits (need >= {intent.MinOreMined})");
    }

    // O4 (window-gated): a Transport delivered — a bare colony (owned, no buildings) now holds ore, which
    // it can only have received from a delivered + auto-unloaded supply run.
    private static OutcomeResult CheckTransportDelivered(WorldSnapshot s, bool expected)
    {
        if (!expected)
        {
            return Skipped("O4", "transport delivered");
        }

        var colonyWithOre = s.Planets.FirstOrDefault(p => IsColony(p) && p.IronOre.GetCurrentValue(s.Now) > 0m);
        var delivered = colonyWithOre is not null;
        var detail = delivered
            ? $"colony {colonyWithOre!.Id} holds {colonyWithOre.IronOre.GetCurrentValue(s.Now)} ore (delivered by transport)"
            : "no bare colony holds any ore";
        return Threshold("O4", "transport delivered", delivered, detail);
    }

    // O5 (window-gated): a depletion fired — some deposit reached 0, at a captured instant or at the
    // final snapshot (GetCurrentValue clamps to [0, cap], so a value of 0 is a genuine empty deposit).
    private static OutcomeResult CheckDepletionFired(WorldSnapshot s, bool expected)
    {
        if (!expected)
        {
            return Skipped("O5", "depletion fired");
        }

        // Require an observed POSITIVE-then-ZERO transition, not merely a zero: a deposit empty from the
        // start (or a planet created empty) must not count as "the run depleted it". The series is
        // time-ordered; the final snapshot is appended as the last observation.
        var timeline = s.DepositSeries
            .OrderBy(snap => snap.At)
            .Select(snap => snap.Deposits)
            .Append(s.Planets.ToDictionary(p => p.Id, p => p.IronOreDeposit.GetCurrentValue(s.Now)))
            .ToList();

        var depletedPlanet = FindDepletedPlanet(timeline);
        return Threshold(
            "O5", "depletion fired", depletedPlanet is not null,
            depletedPlanet is not null
                ? $"deposit on planet {depletedPlanet} went positive -> 0 during the run"
                : "no deposit was observed going from positive to 0");
    }

    // The first planet whose deposit was observed strictly positive and then, at a LATER observation, at
    // 0 — the signature of a depletion that actually happened during the run (deposits clamp to [0, cap],
    // so a non-positive value is exactly 0).
    private static Guid? FindDepletedPlanet(List<IReadOnlyDictionary<Guid, decimal>> timeline)
    {
        var sawPositive = new HashSet<Guid>();
        foreach (var frame in timeline)
        {
            foreach (var (planetId, value) in frame)
            {
                if (value > 0m)
                {
                    sawPositive.Add(planetId);
                }
                else if (sawPositive.Contains(planetId))
                {
                    return planetId;
                }
            }
        }

        return null;
    }

    // O6 (window-gated): both halt cascades were observed — a ResourceDepleted halt (A's drills on the
    // emptied deposit) AND an OutputStorageFull halt (B's refinery on the filled ingot store). Checked
    // across the union of the captured halt series (catches transient halts) and the final snapshot
    // (catches terminal ones the poll cadence may have straddled).
    private static OutcomeResult CheckHaltCascade(WorldSnapshot s, bool expected)
    {
        if (!expected)
        {
            return Skipped("O6", "halt cascade observed");
        }

        var reasons = ObservedHaltReasons(s);
        var depleted = reasons.Contains(HaltReason.ResourceDepleted);
        var storageFull = reasons.Contains(HaltReason.OutputStorageFull);
        var observed = depleted && storageFull;
        return Threshold(
            "O6", "halt cascade observed", observed,
            $"ResourceDepleted={depleted}, OutputStorageFull={storageFull} (need both); all observed reasons: {FormatReasons(reasons)}");
    }

    // Colonies = owned planets beyond the registered homeworlds. Registration claims exactly one
    // homeworld per player, so this equals (owned planets) - (players).
    private static int ColoniesWon(WorldSnapshot s) =>
        s.Planets.Count(p => p.OwnerId is not null) - s.Players.Count;

    // A colony is an owned planet with no live building slots — a homeworld always carries its seeded
    // Drill/Refinery/Generator, a fleet-colonized planet starts bare (and this scenario builds nothing
    // on colonies). Tombstones (Cancelled/Demolished) are not live, mirroring Tier 1's I7 predicate.
    private static bool IsColony(Planet p) =>
        p.OwnerId is not null &&
        !p.Buildings.Any(b => b.Status is not (BuildingStatus.Cancelled or BuildingStatus.Demolished));

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

    private static string FormatReasons(HashSet<HaltReason> reasons) =>
        reasons.Count == 0 ? "none" : string.Join(", ", reasons.OrderBy(r => r).Select(r => r.ToString()));

    private static OutcomeResult Threshold(string id, string title, bool passed, string detail) =>
        new(id, title, passed ? OutcomeStatus.Passed : OutcomeStatus.Failed, detail);

    private static OutcomeResult Skipped(string id, string title) =>
        new(id, title, OutcomeStatus.Skipped, "skipped: needs SOAK_WINDOW_SECONDS >= cascade window");
}
