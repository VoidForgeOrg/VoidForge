using Marten;
using Microsoft.Extensions.Options;
using Voidforge.Api.Documents;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Http;

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
        StageWorld(session, opts);

        try
        {
            // Insert (not Store): Store upserts and would silently overwrite; Insert issues a
            // plain INSERT that trips the primary-key unique violation (23505) on a duplicate.
            // Committing the marker in the SAME session as the world data makes both land in one
            // transaction, so a losing concurrent seeder's marker AND its duplicate world roll
            // back together — the count check above only short-circuits the common already-seeded
            // restart; this marker is what actually arbitrates the genuine TOCTOU race.
            session.Insert(new WorldSeedMarker { Id = WorldSeedMarker.WellKnownId });
            await session.SaveChangesAsync(cancellationToken);
            LogWorldSeeded(logger, opts.SolarSystemCount, opts.PlanetsPerSystem);
        }
        catch (Exception ex) when (MartenExceptions.IsUniqueViolation(ex))
        {
            // Another instance won the seed race; its world is authoritative and this seeder's
            // whole batch rolled back. A duplicate is success, not an error — swallow ONLY the
            // unique violation (a hosted service throwing from StartAsync aborts host startup;
            // anything else must propagate).
            LogWorldAlreadySeeded(logger, opts.SolarSystemCount);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Stages every solar system and planet stream into the session's pending unit of work
    // without saving — the caller commits them together with the seed marker in one transaction.
    // Generation itself is factored into the pure BuildWorld so it can be unit-tested (determinism)
    // without a session or a database.
    private static void StageWorld(IDocumentSession session, WorldGenOptions opts)
    {
        foreach (var planned in BuildWorld(opts))
        {
            foreach (var planet in planned.Planets)
            {
                session.Events.StartStream<Planet>(planet.PlanetId, planet.Event);
            }

            session.Store(planned.System);
        }
    }

    // Pure world generator. Deterministic when opts.Seed is set: coordinates AND ids are drawn from a
    // seeded PRNG in a FIXED order (per system: id → X → Y → Z; per planet: id → X-offset → Y-offset →
    // Z-offset), so the same seed reproduces byte-identical systems and planets. When Seed is null it
    // falls back to an unseeded Random and Guid.NewGuid(), preserving the original behavior.
    internal static IReadOnlyList<PlannedSystem> BuildWorld(WorldGenOptions opts)
    {
        var deterministic = opts.Seed is not null;
        var random = deterministic ? new Random(opts.Seed!.Value) : new Random();

        var systems = new List<PlannedSystem>(opts.SolarSystemCount);
        for (var s = 0; s < opts.SolarSystemCount; s++)
        {
            var systemId = NextId(random, deterministic);
            var systemX = NextCoordinate(random, opts.CoordinateRange);
            var systemY = NextCoordinate(random, opts.CoordinateRange);
            var systemZ = NextCoordinate(random, opts.CoordinateRange);

            var planets = new List<PlannedPlanet>(opts.PlanetsPerSystem);
            var planetIds = new List<Guid>(opts.PlanetsPerSystem);
            for (var p = 0; p < opts.PlanetsPerSystem; p++)
            {
                var planetId = NextId(random, deterministic);
                planetIds.Add(planetId);
                planets.Add(new PlannedPlanet(planetId, new PlanetCreated(
                    Name: $"Planet {s + 1}-{p + 1}",
                    SolarSystemId: systemId,
                    IronOrePool: opts.IronOrePool,
                    BuildingSlotCount: opts.BuildingSlotCount,
                    IronOreStorageCapacity: opts.IronOreStorageCapacity,
                    IronIngotStorageCapacity: opts.IronIngotStorageCapacity,
                    X: systemX + NextCoordinate(random, opts.PlanetSpread),
                    Y: systemY + NextCoordinate(random, opts.PlanetSpread),
                    Z: systemZ + NextCoordinate(random, opts.PlanetSpread))));
            }

            systems.Add(new PlannedSystem(
                new SolarSystem
                {
                    Id = systemId,
                    Name = $"System {s + 1}",
                    X = systemX,
                    Y = systemY,
                    Z = systemZ,
                    PlanetIds = planetIds,
                },
                planets));
        }

        return systems;
    }

    // Deterministic-when-seeded id: fills 16 bytes from the seeded PRNG so the same seed reproduces the
    // same id. The bytes need not form an RFC-valid Guid version/variant — Marten only needs a stable
    // unique key for the stream/document id. Unseeded, falls back to Guid.NewGuid().
    private static Guid NextId(Random random, bool deterministic)
    {
        if (!deterministic)
        {
            return Guid.NewGuid();
        }

        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static decimal NextCoordinate(Random random, decimal range)
    {
        return (decimal)(random.NextDouble() * (double)(range * 2)) - range;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "World already seeded with {Count} solar systems, skipping.")]
    private static partial void LogWorldAlreadySeeded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {SystemCount} solar systems with {PlanetsPerSystem} planets each.")]
    private static partial void LogWorldSeeded(ILogger logger, int systemCount, int planetsPerSystem);
}
