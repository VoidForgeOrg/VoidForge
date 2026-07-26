namespace Voidforge.Api.Domain;

// Energy concern of the Planet aggregate (#40 split).
public sealed partial class Planet
{
    // Energy is a flow resource: derived on demand from the operational building
    // composition, never stored (game-design/resources.md). Methods rather than
    // computed properties so they stay out of the Marten snapshot document.
    public decimal GetEnergyGenerationMw() => Buildings
        .Where(b => b.Status == BuildingStatus.Operational)
        .Sum(b => BuildingSpecs.EnergyOutputMw(b.Type));

    public decimal GetEnergyConsumptionMw()
    {
        var operational = Buildings.Where(b => b.Status == BuildingStatus.Operational).ToList();
        var nonShipyardDraw = operational
            .Where(b => b.Type != BuildingType.Shipyard)
            .Sum(b => BuildingSpecs.EnergyDrawMw(b.Type));

        var shipyardCount = operational.Count(b => b.Type == BuildingType.Shipyard);
        if (shipyardCount == 0)
        {
            return nonShipyardDraw;
        }

        // Fungible bays: work concentrates into as few shipyards as possible. Those drawing full
        // power = ceil(activeBuilds / ParallelBuilds); the rest idle at 5%.
        var full = BuildingSpecs.EnergyDrawMw(BuildingType.Shipyard);
        var activeShipyards = Math.Min(
            shipyardCount,
            (int)Math.Ceiling(ActiveShipBuildCount() / (double)BuildingSpecs.ShipyardParallelBuilds));
        var shipyardDraw = (activeShipyards * full)
            + ((shipyardCount - activeShipyards) * BuildingSpecs.ShipyardIdleDrawFactor * full);

        return nonShipyardDraw + shipyardDraw;
    }

    // In [0, 1]: 1 when demand is met (or there is no demand), generation/consumption
    // when overloaded, 0 when consumers exist but no generator does.
    public decimal GetProductivityMultiplier()
    {
        var consumption = GetEnergyConsumptionMw();
        if (consumption == 0)
        {
            return 1m;
        }

        return Math.Min(1m, GetEnergyGenerationMw() / consumption);
    }
}
