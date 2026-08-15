namespace Voidforge.Api.Domain;

// Read-side scoring weights (#67). Mirrors BuildingSpecs: a static, DI-free rules table consulted
// only by the read-side ScoreCalculator (never inside an aggregate Apply), so the BalanceOptions-DI
// rationale for aggregate purity does not apply here.
//
// Every point value below is a PLACEHOLDER, TBD during balancing. They are chosen only so each asset
// category is distinguishable in tests: planet flat 100; Shipyard > Drill; ColonyShip > CargoVessel;
// ingot worth more per unit than ore.
public static class ScoringSpecs
{
    // Flat points for each colonized planet a player owns.
    public const decimal PointsPerPlanet = 100m;

    public static decimal BuildingPoints(BuildingType type) => type switch
    {
        BuildingType.Shipyard => 40m,
        BuildingType.Refinery => 30m,
        BuildingType.Generator => 25m,
        BuildingType.Drill => 20m,
        _ => 0m,
    };

    public static decimal ShipPoints(ShipType type) => type switch
    {
        ShipType.ColonyShip => 50m,
        ShipType.CargoVessel => 30m,
        _ => 0m,
    };

    public static decimal ResourcePointsPerUnit(ResourceType type) => type switch
    {
        ResourceType.IronIngot => 2m,
        ResourceType.IronOre => 1m,
        _ => 0m,
    };
}
