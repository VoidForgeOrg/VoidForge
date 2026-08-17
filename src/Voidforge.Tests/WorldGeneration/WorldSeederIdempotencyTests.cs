using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Voidforge.Api.Documents;
using Voidforge.Api.Http;
using Voidforge.Api.WorldGeneration;
using Xunit;

namespace Voidforge.Tests.WorldGeneration;

// #46: WorldSeeder.StartAsync used to do a non-atomic read-then-act (count SolarSystems, seed if
// zero), so two concurrent seeders both read zero and each seeded a full world. The fix commits a
// single-row WorldSeedMarker (fixed primary key) in the SAME transaction as the world data, so the
// loser collides on the primary key (23505) and its whole batch rolls back atomically. These tests
// pin both halves of the guard against the shared, already-seeded fixture host.
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class WorldSeederIdempotencyTests
{
    private readonly IServiceProvider _services;

    public WorldSeederIdempotencyTests(AppFixture fixture)
    {
        _services = fixture.Host.Services;
    }

    [Fact]
    public async Task ReSeedingAnAlreadySeededWorldAddsNoNewSolarSystems()
    {
        // DI owns the store's lifetime — resolve it, never dispose it (testing.md pitfall).
        var store = _services.GetRequiredService<IDocumentStore>();
        var options = _services.GetRequiredService<IOptions<WorldGenOptions>>();

        var countBefore = await CountSolarSystemsAsync(store);
        Assert.True(countBefore > 0, "Fixture boot should have seeded a world exactly once.");

        // A second seeder against the already-seeded store must take the count fast-path and return
        // without staging or committing a second world.
        var secondSeeder = new WorldSeeder(store, options, NullLogger<WorldSeeder>.Instance);
        await secondSeeder.StartAsync(CancellationToken.None);
        await secondSeeder.StopAsync(CancellationToken.None);

        var countAfter = await CountSolarSystemsAsync(store);
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task DuplicateSeedMarkerInsertTripsTheUniqueViolationThatArbitratesConcurrentSeeders()
    {
        // The fixture-boot seeder already committed the one WorldSeedMarker. Inserting the same
        // well-known primary key again — exactly what a losing concurrent seeder does inside its own
        // transaction — must fail SaveChanges with a Postgres unique violation (23505). That
        // collision is what rolls the loser's whole batch (marker + duplicate world) back instead of
        // double-seeding, so proving it deterministically proves the race arbitration.
        //
        // A live two-seeder concurrent run is deliberately NOT exercised here: the shared fixture DB
        // is already seeded, so both seeders would take the count fast-path and never contend. Forcing
        // the primary-key collision by hand proves the same mechanism without a separate empty store
        // and without probabilistic timing — matching how the codebase pins its other race guards.
        var store = _services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession();
        // Insert (not Store): an insert-only statement that trips the PK constraint on a duplicate,
        // whereas Store would upsert and silently succeed.
        session.Insert(new WorldSeedMarker { Id = WorldSeedMarker.WellKnownId });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => session.SaveChangesAsync());
        Assert.True(
            MartenExceptions.IsUniqueViolation(exception),
            "A duplicate WorldSeedMarker insert must surface as a Postgres 23505 unique violation.");
    }

    private static async Task<int> CountSolarSystemsAsync(IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        return await session.Query<SolarSystem>().CountAsync();
    }
}
