namespace Voidforge.Api.Domain;

// Intrinsic, balance-tunable stats for each building type. These are domain rules, not
// world-generation knobs. Rates are expressed in units per second to match ResourcePool,
// which accrues value over elapsed TotalSeconds.
//
// The numeric values are sourced from Current — an EconomyRates instance installed ONCE at host
// startup from the "Economy" config section (Program → Configure). It defaults to the balancing
// placeholders in EconomyRates, so pure-domain code and tests that never boot a host read the same
// constants as before. Current is process-global and read during event replay (RebaseRates), so it
// must be fixed before the host serves traffic; differing rates require a separate process. Only the
// values move to config — the per-type switch shape (and the structural ProducedResource mapping)
// stays here.
public static class BuildingSpecs
{
    private static EconomyRates _current = new();

    // The active rate table. Defaults to the balancing placeholders until Configure installs the
    // config-bound values at startup.
    internal static EconomyRates Current => _current;

    // Composition-root hook: install the configured rate table before the host serves traffic.
    internal static void Configure(EconomyRates economy)
    {
        ArgumentNullException.ThrowIfNull(economy);
        _current = economy;
    }

    public static decimal IronOreRatePerSecond(BuildingType type) => type switch
    {
        BuildingType.Drill => _current.DrillOreRatePerSecond,
        _ => 0m,
    };

    // Balance placeholders (TBD during balancing). Homeworld sanity: Generator 100 MW
    // covers the starting Drill (20) + Refinery (30) with headroom.
    public static decimal EnergyOutputMw(BuildingType type) => type switch
    {
        BuildingType.Generator => _current.GeneratorEnergyOutputMw,
        _ => 0m,
    };

    // The Shipyard draws its full rating while operational; the 5%-idle rule arrives
    // with ship builds in Phase 3 PR 5 (#27).
    public static decimal EnergyDrawMw(BuildingType type) => type switch
    {
        BuildingType.Drill => _current.DrillEnergyDrawMw,
        BuildingType.Refinery => _current.RefineryEnergyDrawMw,
        BuildingType.Shipyard => _current.ShipyardEnergyDrawMw,
        _ => 0m,
    };

    // Iron Ore consumed per second by an operational Refinery (input rate). Balance
    // placeholder, TBD during balancing.
    public static decimal RefineryOreConsumptionPerSecond(BuildingType type) => type switch
    {
        BuildingType.Refinery => _current.RefineryOreConsumptionPerSecond,
        _ => 0m,
    };

    // The 1:2 ore→ingot conversion ratio, in one place: ingot output = this × ore consumed.
    public static decimal RefineryIngotOutputFactor => _current.RefineryIngotOutputFactor;

    // Structural domain rules (parallel ship-build bays per operational Shipyard, and the idle-draw
    // fraction a Shipyard consumes with no active builds) — now config-tunable via EconomyRates.
    public static int ShipyardParallelBuilds => _current.ShipyardParallelBuilds;
    public static decimal ShipyardIdleDrawFactor => _current.ShipyardIdleDrawFactor;

    // Fraction of full rating a Halted building draws (#69). Same 5% idle floor as a shipyard.
    public static decimal HaltedDrawFactor => _current.HaltedDrawFactor;

    // The stored resource a building produces into, or null for buildings with no stored output
    // (Generator, Shipyard). Drives output-storage halting (#69). Structural mapping, not a balance
    // knob — stays a hardcoded rule.
    public static ResourceType? ProducedResource(BuildingType type) => type switch
    {
        BuildingType.Drill => ResourceType.IronOre,
        BuildingType.Refinery => ResourceType.IronIngot,
        _ => null,
    };
}
