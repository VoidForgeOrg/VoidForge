using Voidforge.Api.WorldGeneration;
using Xunit;

namespace Voidforge.Tests.WorldGeneration;

[Trait("Category", "Unit")]
public sealed class WorldSeederDeterminismTests
{
    private static WorldGenOptions Options(int? seed) => new()
    {
        SolarSystemCount = 4,
        PlanetsPerSystem = 3,
        Seed = seed,
    };

    // A fingerprint of everything that must be reproducible: every system/planet id and coordinate.
    private static List<string> Fingerprint(IReadOnlyList<PlannedSystem> world)
    {
        var lines = new List<string>();
        foreach (var s in world)
        {
            lines.Add($"S:{s.System.Id}:{s.System.X}:{s.System.Y}:{s.System.Z}");
            foreach (var p in s.Planets)
            {
                lines.Add($"P:{p.PlanetId}:{p.Event.X}:{p.Event.Y}:{p.Event.Z}:{p.Event.Name}");
            }
        }

        return lines;
    }

    [Fact]
    public void SameSeedProducesIdenticalWorld()
    {
        var first = WorldSeeder.BuildWorld(Options(1234));
        var second = WorldSeeder.BuildWorld(Options(1234));

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void DifferentSeedProducesDifferentWorld()
    {
        var first = WorldSeeder.BuildWorld(Options(1));
        var second = WorldSeeder.BuildWorld(Options(2));

        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void SeedProducesRequestedStructure()
    {
        var world = WorldSeeder.BuildWorld(Options(7));

        Assert.Equal(4, world.Count);
        Assert.All(world, s => Assert.Equal(3, s.Planets.Count));
        // Every id is distinct across the whole world.
        var ids = world.SelectMany(s => s.Planets.Select(p => p.PlanetId)).Concat(world.Select(s => s.System.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NoSeedStillProducesRequestedCounts()
    {
        var world = WorldSeeder.BuildWorld(Options(seed: null));

        Assert.Equal(4, world.Count);
        Assert.All(world, s => Assert.Equal(3, s.Planets.Count));
    }
}
