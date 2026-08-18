namespace Voidforge.Api.Domain;

// Tunable numeric values behind BuildingSpecs, bound from the "Economy" configuration section
// (verifier tooling / balancing). Mutable properties so the config binder can override individual
// leaves; any unset leaf keeps the balancing-placeholder default — identical to the former
// BuildingSpecs constants — so an absent "Economy" section changes nothing.
//
// Installed into BuildingSpecs.Current ONCE at host startup (Program). Lives in Domain rather than
// Balance because these ARE the domain rate rules: the aggregate reads them through the static
// BuildingSpecs (never via DI), which keeps Apply/RebaseRates pure and replay-safe. Rates are fixed
// for the process lifetime, so replay is deterministic within a run.
public sealed class EconomyRates
{
    // Ore extracted per second by an operational Drill.
    public decimal DrillOreRatePerSecond { get; set; } = 10m;

    // Ore consumed per second by an operational Refinery (its input rate).
    public decimal RefineryOreConsumptionPerSecond { get; set; } = 5m;

    // The ore->ingot conversion ratio: ingot output = this * ore consumed.
    public decimal RefineryIngotOutputFactor { get; set; } = 2m;

    // Energy generated per second (MW) by an operational Generator.
    public decimal GeneratorEnergyOutputMw { get; set; } = 100m;

    // Energy drawn (MW) by an operational Drill / Refinery / Shipyard.
    public decimal DrillEnergyDrawMw { get; set; } = 20m;
    public decimal RefineryEnergyDrawMw { get; set; } = 30m;
    public decimal ShipyardEnergyDrawMw { get; set; } = 40m;

    // Fraction of full rating a Halted building draws (#69).
    public decimal HaltedDrawFactor { get; set; } = 0.05m;

    // Fraction of full rating an operational Shipyard with no active builds draws (#27).
    public decimal ShipyardIdleDrawFactor { get; set; } = 0.05m;

    // Parallel ship-build bays per operational Shipyard.
    public int ShipyardParallelBuilds { get; set; } = 3;
}
