namespace Voidforge.Api.Balance;

// Per-ship-type speed and cargo capacity. Mutable properties so the .NET configuration
// binder can override individual values from the "Balance" section (e.g. different speeds
// in tests). Balance placeholders, TBD during balancing.
public sealed class ShipBalance
{
    public decimal SpeedPerSecond { get; set; }
    public decimal CargoCapacity { get; set; }
}
