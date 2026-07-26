using Voidforge.Api.Domain;

namespace Voidforge.Api.Balance;

// Balance values bound from the "Balance" configuration section (test hosts override
// durations for fast end-to-end tests). Deliberately DI options rather than a static
// (spec decision D10 honored in intent, safer mechanism): keeps aggregate Apply/RebaseRates
// pure and unit tests hermetic. Defaults are the spec §6 placeholders.
public sealed class BalanceOptions
{
    public ConstructionBalance Drill { get; set; } = new() { IngotCost = 300m, BuildDurationSeconds = 60m };
    public ConstructionBalance Refinery { get; set; } = new() { IngotCost = 450m, BuildDurationSeconds = 90m };
    public ConstructionBalance Generator { get; set; } = new() { IngotCost = 240m, BuildDurationSeconds = 60m };
    public ConstructionBalance Shipyard { get; set; } = new() { IngotCost = 600m, BuildDurationSeconds = 120m };

    public ConstructionBalance ColonyShip { get; set; } = new() { IngotCost = 1000m, BuildDurationSeconds = 300m };
    public ConstructionBalance CargoVessel { get; set; } = new() { IngotCost = 400m, BuildDurationSeconds = 120m };

    public ShipsBalanceOptions Ships { get; set; } = new();

    public ConstructionBalance ForBuilding(BuildingType type) => type switch
    {
        BuildingType.Drill => Drill,
        BuildingType.Refinery => Refinery,
        BuildingType.Generator => Generator,
        BuildingType.Shipyard => Shipyard,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown building type."),
    };

    public ConstructionBalance ForShip(ShipType type) => type switch
    {
        ShipType.ColonyShip => ColonyShip,
        ShipType.CargoVessel => CargoVessel,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ship type."),
    };
}
