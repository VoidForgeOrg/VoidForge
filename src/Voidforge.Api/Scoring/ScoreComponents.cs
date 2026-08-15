using Voidforge.Api.Domain;

namespace Voidforge.Api.Scoring;

// The reuse seam (#67 → #68): a pure, aggregate-free snapshot of a player's scorable assets.
// ScoreCalculator.Extract builds it from the Planet/Fleet aggregates; ScoreCalculator.Score turns it
// into points via ScoringSpecs. #68's leaderboard projection will persist THIS per player and re-run
// Score without re-reading aggregates.
//
// Counts are keyed by type so the points math stays type-driven. Resource totals are decimal because
// the pools are decimal — no premature rounding.
public sealed record ScoreComponents(
    int PlanetCount,
    IReadOnlyDictionary<BuildingType, int> BuildingCounts,
    IReadOnlyDictionary<ShipType, int> ShipCounts,
    decimal IronOre,
    decimal IronIngot);
