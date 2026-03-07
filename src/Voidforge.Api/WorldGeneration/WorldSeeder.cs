using Marten;
using Microsoft.Extensions.Options;
using Voidforge.Api.Documents;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.WorldGeneration;

public sealed partial class WorldSeeder(
    IDocumentStore store,
    IOptions<WorldGenOptions> options,
    ILogger<WorldSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();

        var existingCount = await session.Query<SolarSystem>().CountAsync(cancellationToken);
        if (existingCount > 0)
        {
            LogWorldAlreadySeeded(logger, existingCount);
            return;
        }

        var opts = options.Value;
        var random = new Random(42);

        for (var s = 0; s < opts.SolarSystemCount; s++)
        {
            var systemId = Guid.NewGuid();
            var planetIds = new List<Guid>();

            for (var p = 0; p < opts.PlanetsPerSystem; p++)
            {
                var planetId = Guid.NewGuid();
                planetIds.Add(planetId);

                session.Events.StartStream<Planet>(planetId, new PlanetCreated(
                    Name: $"Planet {s + 1}-{p + 1}",
                    SolarSystemId: systemId,
                    IronOrePool: opts.IronOrePool,
                    BuildingSlotCount: opts.BuildingSlotCount,
                    IronOreStorageCapacity: opts.IronOreStorageCapacity,
                    IronIngotStorageCapacity: opts.IronIngotStorageCapacity));
            }

            session.Store(new SolarSystem
            {
                Id = systemId,
                Name = $"System {s + 1}",
                X = NextCoordinate(random, opts.CoordinateRange),
                Y = NextCoordinate(random, opts.CoordinateRange),
                Z = NextCoordinate(random, opts.CoordinateRange),
                PlanetIds = planetIds,
            });
        }

        await session.SaveChangesAsync(cancellationToken);

        LogWorldSeeded(logger, opts.SolarSystemCount, opts.PlanetsPerSystem);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static decimal NextCoordinate(Random random, decimal range)
    {
        return (decimal)(random.NextDouble() * (double)(range * 2)) - range;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "World already seeded with {Count} solar systems, skipping.")]
    private static partial void LogWorldAlreadySeeded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {SystemCount} solar systems with {PlanetsPerSystem} planets each.")]
    private static partial void LogWorldSeeded(ILogger logger, int systemCount, int planetsPerSystem);
}
