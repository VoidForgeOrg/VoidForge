using Voidforge.Api.Domain;

namespace Voidforge.Api.Scoring;

// Read-side scoring weights, bound from the "Scoring" configuration section (verifier tooling /
// balancing). Mutable properties so the config binder can override individual leaves; defaults mirror
// the ScoringSpecs constants (kept as the single source of the placeholder values), so an absent
// "Scoring" section — and the parameterless ScoreCalculator used in unit tests — score exactly as
// before. No replay concern: scoring is computed on the read side from live aggregates, never inside
// an aggregate Apply, so this is plain DI (unlike the economy rates).
public sealed class ScoringOptions
{
    public decimal PointsPerPlanet { get; set; } = ScoringSpecs.PointsPerPlanet;

    public decimal DrillPoints { get; set; } = ScoringSpecs.BuildingPoints(BuildingType.Drill);
    public decimal RefineryPoints { get; set; } = ScoringSpecs.BuildingPoints(BuildingType.Refinery);
    public decimal GeneratorPoints { get; set; } = ScoringSpecs.BuildingPoints(BuildingType.Generator);
    public decimal ShipyardPoints { get; set; } = ScoringSpecs.BuildingPoints(BuildingType.Shipyard);

    public decimal ColonyShipPoints { get; set; } = ScoringSpecs.ShipPoints(ShipType.ColonyShip);
    public decimal CargoVesselPoints { get; set; } = ScoringSpecs.ShipPoints(ShipType.CargoVessel);

    public decimal IronOrePointsPerUnit { get; set; } = ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronOre);
    public decimal IronIngotPointsPerUnit { get; set; } = ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronIngot);

    public decimal BuildingPoints(BuildingType type) => type switch
    {
        BuildingType.Shipyard => ShipyardPoints,
        BuildingType.Refinery => RefineryPoints,
        BuildingType.Generator => GeneratorPoints,
        BuildingType.Drill => DrillPoints,
        _ => 0m,
    };

    public decimal ShipPoints(ShipType type) => type switch
    {
        ShipType.ColonyShip => ColonyShipPoints,
        ShipType.CargoVessel => CargoVesselPoints,
        _ => 0m,
    };

    public decimal ResourcePointsPerUnit(ResourceType type) => type switch
    {
        ResourceType.IronIngot => IronIngotPointsPerUnit,
        ResourceType.IronOre => IronOrePointsPerUnit,
        _ => 0m,
    };
}
