using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.SoakTests;

// The Tier-1 invariants I1–I11 that must hold for ANY legal run, evaluated against a single
// authoritative <see cref="WorldSnapshot"/>. Caps/limits are read off the aggregates and the injected
// options, never hardcoded. Evaluate returns a structured report; AssertAll turns it into a hard fail.
public static class Tier1Invariants
{
    public static Tier1Report Evaluate(
        WorldSnapshot s, Func<ShipType, decimal> cargoCapacityOf, TimeSpan overdueMargin)
    {
        var results = new List<InvariantResult>
        {
            CheckOreAndDepositBounds(s),
            CheckIngotBounds(s),
            CheckDeadLetters(s),
            CheckNoServerErrors(s),
            CheckRacedStatusesClean(s),
            CheckNothingStuck(s, overdueMargin),
            CheckLiveSlotCount(s),
            CheckShipUniqueness(s),
            CheckCargoBounds(s, cargoCapacityOf),
            CheckProductivityBounds(s),
            CheckDepositMonotonicity(s),
        };
        return new Tier1Report(results);
    }

    public static void AssertAll(Tier1Report report) =>
        Assert.True(
            report.AllPassed,
            "Tier-1 invariant(s) violated:" + Environment.NewLine + report.FailureSummary());

    // I1: every planet's stored ore and its finite ore deposit sit within [0, cap]; the deposit cap is
    // its seeded initial value (IronOreDeposit.StorageCapacity).
    private static InvariantResult CheckOreAndDepositBounds(WorldSnapshot s)
    {
        var v = new List<string>();
        foreach (var p in s.Planets)
        {
            AddIfOutOfBounds(v, p.Id, "IronOre", p.IronOre, s.Now);
            AddIfOutOfBounds(v, p.Id, "IronOreDeposit", p.IronOreDeposit, s.Now);
        }

        return new InvariantResult("I1", "planet ore & deposit pools within [0, cap]", v);
    }

    // I2: every planet's stored ingots sit within [0, cap].
    private static InvariantResult CheckIngotBounds(WorldSnapshot s)
    {
        var v = new List<string>();
        foreach (var p in s.Planets)
        {
            AddIfOutOfBounds(v, p.Id, "IronIngot", p.IronIngot, s.Now);
        }

        return new InvariantResult("I2", "planet ingot pools within [0, cap]", v);
    }

    private static void AddIfOutOfBounds(
        List<string> v, Guid planetId, string pool, ResourcePool resource, DateTimeOffset now)
    {
        var value = resource.GetCurrentValue(now);
        if (value < 0m || value > resource.StorageCapacity)
        {
            v.Add($"planet {planetId} {pool}={value} outside [0, {resource.StorageCapacity}]");
        }
    }

    // I3: no message reached the Wolverine dead-letter queue.
    private static InvariantResult CheckDeadLetters(WorldSnapshot s)
    {
        var v = new List<string>();
        if (s.DeadLetterCount != 0)
        {
            v.Add($"wolverine_dead_letters count = {s.DeadLetterCount}");
        }

        return new InvariantResult("I3", "no dead-lettered messages", v);
    }

    // I4: no recorded response was an unexpected server error. 503 (Service Unavailable) is a MODELED
    // capacity outcome the engine returns for NoUncolonizedPlanets, so it is allowed per the plan's I4
    // definition; a 500/502/504 means an unhandled exception escaped a handler.
    //
    // Only deliberately-raced calls (PostForStatus/CancelForStatus) contribute to s.HttpStatuses, so this
    // snapshot check gates those raced paths. A 5xx that surfaces through an asserting helper is NOT
    // recorded here — but it is no longer swallowed either: the harness raises ServerErrorException, which
    // ScenarioScript.IsExpected does NOT match, so it propagates and fails the run outright. Between the
    // two, every path is covered: raced calls by this invariant, asserting helpers by the tripwire.
    private static InvariantResult CheckNoServerErrors(WorldSnapshot s)
    {
        var v = s.HttpStatuses
            .Where(c => c is >= 500 and < 600 and not 503)
            .Distinct()
            .Select(c => $"recorded server-error status {c}")
            .ToList();
        return new InvariantResult("I4", "no unexpected 5xx responses (503 is a modeled outcome)", v);
    }

    // I5: a deliberately-raced call resolves only as a modeled outcome — 200 (won), 403 (foreign
    // destination / not owner), 409 (concurrency loss), or 503 (capacity) — never a 500 or any other
    // unexpected code.
    private static InvariantResult CheckRacedStatusesClean(WorldSnapshot s)
    {
        var v = s.HttpStatuses
            .Where(c => c is not (200 or 403 or 409 or 503))
            .Distinct()
            .Select(c => $"unexpected raced status {c}")
            .ToList();
        return new InvariantResult("I5", "raced calls resolve as 200/403/409/503 (never 500)", v);
    }

    // I6: after the drain nothing is overdue past the margin — no building still UnderConstruction, no
    // Active ship build, no InTransit fleet past its deadline. Halted / ConstructionHalted are MODELED
    // states, not stuck, so they are excluded.
    private static InvariantResult CheckNothingStuck(WorldSnapshot s, TimeSpan margin)
    {
        var cutoff = s.Now - margin;
        var v = new List<string>();
        foreach (var p in s.Planets)
        {
            v.AddRange(p.Buildings.Where(b => IsBuildingStuck(b, cutoff))
                .Select(b => $"planet {p.Id} building {b.Type} overdue UnderConstruction"));
            v.AddRange(p.ShipQueue.Where(b => IsShipBuildStuck(b, cutoff))
                .Select(b => $"planet {p.Id} ship build {b.Id} overdue Active"));
        }

        v.AddRange(s.Fleets.Where(f => IsFleetStuck(f, cutoff))
            .Select(f => $"fleet {f.Id} overdue InTransit"));
        return new InvariantResult("I6", "nothing overdue past the drain margin", v);
    }

    private static bool IsBuildingStuck(BuildingSlot b, DateTimeOffset cutoff) =>
        b.Status == BuildingStatus.UnderConstruction && b.CompletesAt is { } at && at < cutoff;

    private static bool IsShipBuildStuck(ShipBuild b, DateTimeOffset cutoff) =>
        b.Status == ShipBuildStatus.Active && b.CompletesAt is { } at && at < cutoff;

    private static bool IsFleetStuck(Fleet f, DateTimeOffset cutoff) =>
        f.Status == FleetStatus.InTransit && f.ArrivesAt is { } at && at < cutoff;

    // I7: the live building-slot count (excluding the Cancelled/Demolished tombstones) never exceeds
    // the planet's slot cap.
    private static InvariantResult CheckLiveSlotCount(WorldSnapshot s)
    {
        var v = new List<string>();
        foreach (var p in s.Planets)
        {
            var live = p.Buildings.Count(b => b.Status is not (BuildingStatus.Cancelled or BuildingStatus.Demolished));
            if (live > p.BuildingSlotCount)
            {
                v.Add($"planet {p.Id} has {live} live slots > cap {p.BuildingSlotCount}");
            }
        }

        return new InvariantResult("I7", "live building slots within the planet cap", v);
    }

    // I8: every ship id appears at most once across planet rosters, ship queues, and non-Disbanded
    // fleets (mirrors ScoreCalculator.CountShips' no-double-count rule).
    private static InvariantResult CheckShipUniqueness(WorldSnapshot s)
    {
        var seen = new HashSet<Guid>();
        var v = new List<string>();

        void Register(Guid id, string where)
        {
            if (!seen.Add(id))
            {
                v.Add($"ship {id} appears more than once (again in {where})");
            }
        }

        foreach (var p in s.Planets)
        {
            foreach (var ship in p.Ships)
            {
                Register(ship.Id, $"planet {p.Id} roster");
            }

            foreach (var build in p.ShipQueue)
            {
                Register(build.Id, $"planet {p.Id} queue");
            }
        }

        foreach (var f in s.Fleets.Where(f => f.Status != FleetStatus.Disbanded))
        {
            foreach (var ship in f.Ships)
            {
                Register(ship.Id, $"fleet {f.Id}");
            }
        }

        return new InvariantResult("I8", "ship ids unique across rosters, queues, and live fleets", v);
    }

    // I9: every non-Disbanded fleet has non-negative cargo within its combined ship capacity.
    private static InvariantResult CheckCargoBounds(WorldSnapshot s, Func<ShipType, decimal> capacityOf)
    {
        var v = new List<string>();
        foreach (var f in s.Fleets.Where(f => f.Status != FleetStatus.Disbanded))
        {
            if (f.CargoIronOre < 0m || f.CargoIronIngot < 0m)
            {
                v.Add($"fleet {f.Id} negative cargo (ore={f.CargoIronOre}, ingot={f.CargoIronIngot})");
            }

            var load = f.GetCargoLoad();
            var capacity = f.GetCargoCapacity(capacityOf);
            if (load > capacity)
            {
                v.Add($"fleet {f.Id} cargo load {load} exceeds capacity {capacity}");
            }
        }

        return new InvariantResult("I9", "fleet cargo non-negative and within capacity", v);
    }

    // I10: every planet's energy productivity multiplier stays within [0, 1].
    private static InvariantResult CheckProductivityBounds(WorldSnapshot s)
    {
        var v = new List<string>();
        foreach (var p in s.Planets)
        {
            var m = p.GetProductivityMultiplier();
            if (m < 0m || m > 1m)
            {
                v.Add($"planet {p.Id} productivity multiplier {m} outside [0, 1]");
            }
        }

        return new InvariantResult("I10", "planet productivity multiplier within [0, 1]", v);
    }

    // I11: at every intermediate snapshot each planet's deposit is non-negative, and across each
    // consecutive pair it never rises (the deposit's Rate is always <= 0, so it is monotone).
    private static InvariantResult CheckDepositMonotonicity(WorldSnapshot s)
    {
        var v = new List<string>();
        var series = s.DepositSeries;
        foreach (var snap in series)
        {
            foreach (var (planetId, deposit) in snap.Deposits)
            {
                if (deposit < 0m)
                {
                    v.Add($"planet {planetId} deposit negative ({deposit}) at {snap.At}");
                }
            }
        }

        for (var i = 1; i < series.Count; i++)
        {
            AddDepositRegressions(v, series[i - 1], series[i]);
        }

        return new InvariantResult("I11", "planet deposits non-negative and non-increasing", v);
    }

    private static void AddDepositRegressions(List<string> v, IntermediateSnapshot previous, IntermediateSnapshot current)
    {
        foreach (var (planetId, deposit) in current.Deposits)
        {
            if (previous.Deposits.TryGetValue(planetId, out var earlier) && deposit > earlier)
            {
                v.Add($"planet {planetId} deposit rose {earlier} -> {deposit} between {previous.At} and {current.At}");
            }
        }
    }
}
