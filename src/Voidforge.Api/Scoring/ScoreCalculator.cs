using Microsoft.Extensions.Options;
using Voidforge.Api.Domain;

namespace Voidforge.Api.Scoring;

// Read-side score service (#67), DI-registered singleton. Two layers so #68's leaderboard projection
// can reuse the points math without touching aggregates:
//   Score(components)          — pure: applies ScoringSpecs. #68 reuses THIS.
//   Extract(planets, fleets, now) — builds ScoreComponents from aggregates, evaluating resource pools
//                                   at `now` (checkpoint-lazy, never a stored/stale field) and applying
//                                   the tombstone/alive and no-double-count rules from
//                                   game-design/scoring.md §"Score Inputs".
// Compute is the convenience composition of the two.
public sealed class ScoreCalculator
{
    private readonly ScoringOptions _scoring;

    // Bound from the "Scoring" configuration section via DI. The optional/null default keeps the
    // parameterless construction used by unit tests scoring exactly as the ScoringSpecs defaults
    // (ScoringOptions defaults mirror those constants).
    public ScoreCalculator(IOptions<ScoringOptions>? options = null)
    {
        _scoring = options?.Value ?? new ScoringOptions();
    }

    // Pure: points = planets + buildings + ships + resources, all weighted by the configured scoring
    // options. Decimal end to end (resources are decimal) — no premature rounding; final rounding is a
    // balancing concern.
    public decimal Score(ScoreComponents components)
    {
        var planetPoints = components.PlanetCount * _scoring.PointsPerPlanet;

        var buildingPoints = components.BuildingCounts
            .Sum(kvp => _scoring.BuildingPoints(kvp.Key) * kvp.Value);

        var shipPoints = components.ShipCounts
            .Sum(kvp => _scoring.ShipPoints(kvp.Key) * kvp.Value);

        var resourcePoints =
            (components.IronOre * _scoring.ResourcePointsPerUnit(ResourceType.IronOre))
            + (components.IronIngot * _scoring.ResourcePointsPerUnit(ResourceType.IronIngot));

        return planetPoints + buildingPoints + shipPoints + resourcePoints;
    }

    // Builds components from the player's owned aggregates. The endpoint queries ALL owned fleets, so the
    // non-Disbanded filter (a Disbanded fleet is a terminal tombstone — Ships cleared, cargo forbidden
    // aboard) lives here rather than at the call site.
    public ScoreComponents Extract(
        IReadOnlyCollection<Planet> ownedPlanets,
        IReadOnlyCollection<Fleet> ownedFleets,
        DateTimeOffset now)
    {
        var liveFleets = ownedFleets.Where(f => f.Status != FleetStatus.Disbanded).ToList();
        var (ore, ingot) = SumResources(ownedPlanets, liveFleets, now);

        return new ScoreComponents(
            PlanetCount: ownedPlanets.Count,
            BuildingCounts: CountBuildings(ownedPlanets),
            ShipCounts: CountShips(ownedPlanets, liveFleets),
            IronOre: ore,
            IronIngot: ingot);
    }

    // Convenience composition — the endpoint's one-liner.
    public decimal Compute(
        IReadOnlyCollection<Planet> ownedPlanets,
        IReadOnlyCollection<Fleet> ownedFleets,
        DateTimeOffset now)
        => Score(Extract(ownedPlanets, ownedFleets, now));

    // Every building except the terminal tombstones (Cancelled, Demolished). INCLUDES Operational,
    // UnderConstruction, Halted, ConstructionHalted, Demolishing — scoring counts current state,
    // incomplete assets included (game-design/scoring.md).
    private static Dictionary<BuildingType, int> CountBuildings(IReadOnlyCollection<Planet> planets)
    {
        var counts = new Dictionary<BuildingType, int>();
        foreach (var planet in planets)
        {
            foreach (var slot in planet.Buildings)
            {
                if (slot.Status is BuildingStatus.Cancelled or BuildingStatus.Demolished)
                {
                    continue;
                }

                counts[slot.Type] = counts.GetValueOrDefault(slot.Type) + 1;
            }
        }

        return counts;
    }

    // A ship is in exactly one place at a time: assembly atomically MOVES it out of Planet.Ships into
    // Fleet.Ships (Planet.RemoveShipsFromRoster + Fleet.Assemble committed in one SaveChanges), and
    // disband reverses it. So ids never overlap in persisted state; the HashSet is a defensive backstop
    // that also collapses any transient double-appearance, per the plan's no-double-count rule.
    private static Dictionary<ShipType, int> CountShips(
        IReadOnlyCollection<Planet> planets, IReadOnlyCollection<Fleet> liveFleets)
    {
        var seen = new HashSet<Guid>();
        var counts = new Dictionary<ShipType, int>();

        void Add(Guid id, ShipType type)
        {
            if (seen.Add(id))
            {
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }

        foreach (var planet in planets)
        {
            foreach (var ship in planet.Ships)
            {
                Add(ship.Id, ship.Type);
            }

            // Every ShipQueue entry is alive: cancel and complete physically REMOVE the entry
            // (Apply(ShipConstructionCancelled/ShipCompleted)), so there is no tombstone status —
            // Queued, Active and Halted all denote an in-progress build.
            foreach (var build in planet.ShipQueue)
            {
                Add(build.Id, build.Type);
            }
        }

        foreach (var fleet in liveFleets)
        {
            foreach (var ship in fleet.Ships)
            {
                Add(ship.Id, ship.Type);
            }
        }

        return counts;
    }

    // Σ planet storage pools evaluated at `now` (checkpoint-lazy — the acceptance criterion) + Σ fleet
    // cargo (already decimal totals on the snapshot).
    private static (decimal Ore, decimal Ingot) SumResources(
        IReadOnlyCollection<Planet> planets, IReadOnlyCollection<Fleet> liveFleets, DateTimeOffset now)
    {
        var ore = 0m;
        var ingot = 0m;

        foreach (var planet in planets)
        {
            ore += planet.IronOre.GetCurrentValue(now);
            ingot += planet.IronIngot.GetCurrentValue(now);
        }

        foreach (var fleet in liveFleets)
        {
            ore += fleet.CargoIronOre;
            ingot += fleet.CargoIronIngot;
        }

        return (ore, ingot);
    }
}
