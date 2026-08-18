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
    public decimal PlanetSpread { get; set; } = 20m;
    public long StartingIronOre { get; set; } = 500;
    public long StartingIronIngots { get; set; } = 100;

    // Determinism seed for world generation (verifier tooling). When set, planet/solar-system
    // coordinates AND ids are generated from a seeded PRNG, so the same seed reproduces the same
    // starting board, and homeworld selection becomes deterministic (PlayerEndpoints picks the
    // lowest-id uncolonized planet instead of a random one). Null (the default) preserves the
    // original nondeterministic behavior (unseeded Random + Guid.NewGuid + random homeworld pick).
    public int? Seed { get; set; }
}
