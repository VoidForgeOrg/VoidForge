namespace Voidforge.Api.WorldGeneration;

public sealed class WorldGenOptions
{
    public int SolarSystemCount { get; set; } = 5;
    public int PlanetsPerSystem { get; set; } = 3;
    public long IronOrePool { get; set; } = 50000;
    public int BuildingSlotCount { get; set; } = 6;
    public long IronOreStorageCapacity { get; set; } = 10000;
    public long IronIngotStorageCapacity { get; set; } = 5000;
    public decimal CoordinateRange { get; set; } = 1000;
}
