namespace Voidforge.Api.Domain;

// The physical resources with stored pools (game-design/resources.md). Buildings produce into a
// specific pool (see BuildingSpecs.ProducedResource); output-storage halting keys off that pool.
public enum ResourceType
{
    IronOre,
    IronIngot,
}
