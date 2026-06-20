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
}
