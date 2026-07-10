namespace Voidforge.Api.Domain;

// Intrinsic, balance-tunable stats for each building type. These are domain rules, not
// world-generation knobs. Rates are expressed in units per second to match ResourcePool,
// which accrues value over elapsed TotalSeconds.
public static class BuildingSpecs
{
    public static decimal IronOreRatePerSecond(BuildingType type) => type switch
    {
        BuildingType.Drill => 10m,
        _ => 0m,
    };

    // Balance placeholders (TBD during balancing). Homeworld sanity: Generator 100 MW
    // covers the starting Drill (20) + Refinery (30) with headroom.
    public static decimal EnergyOutputMw(BuildingType type) => type switch
    {
        BuildingType.Generator => 100m,
        _ => 0m,
    };

    // The Shipyard draws its full rating while operational; the 5%-idle rule arrives
    // with ship builds in Phase 3 PR 5 (#27).
    public static decimal EnergyDrawMw(BuildingType type) => type switch
    {
        BuildingType.Drill => 20m,
        BuildingType.Refinery => 30m,
        BuildingType.Shipyard => 40m,
        _ => 0m,
    };

    // Iron Ore consumed per second by an operational Refinery (input rate). Balance
    // placeholder, TBD during balancing.
    public static decimal RefineryOreConsumptionPerSecond(BuildingType type) => type switch
    {
        BuildingType.Refinery => 5m,
        _ => 0m,
    };

    // The 1:2 ore→ingot conversion ratio, in one place: ingot output = this × ore consumed.
    public const decimal RefineryIngotOutputFactor = 2m;
}
