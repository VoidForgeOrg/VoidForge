namespace Voidforge.Api.Balance;

// Per-building-type construction cost/time. Mutable properties so the .NET configuration
// binder can override individual values from the "Balance" section (e.g. short durations
// in tests). Balance placeholders, TBD during balancing.
public sealed class ConstructionBalance
{
    public decimal IngotCost { get; set; }
    public decimal BuildDurationSeconds { get; set; }

    // Continuous ingot consumption while UnderConstruction.
    public decimal DrainPerSecond => BuildDurationSeconds <= 0 ? 0m : IngotCost / BuildDurationSeconds;
}
