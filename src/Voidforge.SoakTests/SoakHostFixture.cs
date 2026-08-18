using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Voidforge.SoakTests;

// Clone of AppFixture for the soak run: applies the SoakConfig env overrides, auto-creates the
// isolated soak DB, drops the schema so WorldSeeder reseeds fresh, then boots the real host.
public sealed class SoakHostFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    // The DI-owned document store. NEVER `await using` this — that disposes the singleton the host
    // still depends on (see technical-design/testing.md's Marten read discipline).
    public IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();

    public async Task InitializeAsync()
    {
        SoakConfig.ApplyEnvironmentOverrides();

        var connStr = SoakConfig.ConnectionString;

        // Safety check FIRST: refuse to touch (or create) a database whose name does not contain "test".
        var builder = new NpgsqlConnectionStringBuilder(connStr);
        if (builder.Database is not { } db || !db.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset database '{builder.Database}'. Soak connection string must target a database containing 'test' in its name.");
        }

        await EnsureDatabaseExistsAsync(connStr, db);

        // Drop the schema before the host starts so the WorldSeeder re-seeds fresh data.
        await using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS voidforge CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        Host = await AlbaHost.For<Program>();
    }

    public Task DisposeAsync() => Host?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    // start-infra only provisions voidforge_test; the soak DB is separate, so create it if missing to
    // keep the run self-contained. Connect to the `voidforge` maintenance DB on the same server and,
    // because CREATE DATABASE cannot run inside a transaction, issue it as a plain command.
    private static async Task EnsureDatabaseExistsAsync(string connStr, string targetDb)
    {
        var admin = new NpgsqlConnectionStringBuilder(connStr) { Database = "voidforge" };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name;";
            check.Parameters.AddWithValue("name", targetDb);
            var exists = await check.ExecuteScalarAsync();
            if (exists is not null)
            {
                return;
            }
        }

        await using var create = conn.CreateCommand();
        // A database identifier cannot be parameterized. targetDb is our own constant/env value and the
        // caller has already required it to contain "test", so this interpolation is safe.
        create.CommandText = $"CREATE DATABASE \"{targetDb}\";";
        await create.ExecuteNonQueryAsync();
    }
}
