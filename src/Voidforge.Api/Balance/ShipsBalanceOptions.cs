using Voidforge.Api.Domain;

namespace Voidforge.Api.Balance;

// Ship-type speed and cargo balance options. Defaults are the spec §6 placeholders.
public sealed class ShipsBalanceOptions
{
    public ShipBalance ColonyShip { get; set; } = new() { SpeedPerSecond = 0.05m, CargoCapacity = 0m };
    public ShipBalance CargoVessel { get; set; } = new() { SpeedPerSecond = 0.10m, CargoCapacity = 500m };

    public ShipBalance For(ShipType type) => type switch
    {
        ShipType.ColonyShip => ColonyShip,
        ShipType.CargoVessel => CargoVessel,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ship type."),
    };
}
