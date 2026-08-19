using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Voidforge.SoakTests;

// Boots the real host for ONE scenario: composes the scenario's connection string from its DbName,
// auto-creates + reseeds that isolated DB, applies the scenario theme, then boots. A subclass names the
// scenario; everything else is shared. Mirrors AppFixture's env-var-before-boot approach (the
// WithWebHostBuilder-avoiding path) so it dodges the .NET 9 disposal race.
public abstract class SoakHostFixture : IAsyncLifetime
{
    // The scenario this fixture hosts. Each concrete subclass names exactly one.
    protected abstract SoakScenario Scenario { get; }

    // Exposed so SoakRunner can read the scenario's Id / Intent / BaselineFile without re-selecting it.
    public SoakScenario ActiveScenario => Scenario;

    public IAlbaHost Host { get; private set; } = null!;

    // The DI-owned document store. NEVER `await using` this — that disposes the singleton the host
    // still depends on (see technical-design/testing.md's Marten read discipline).
    public IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();

    public async Task InitializeAsync()
    {
        var connStr = SoakConfig.ConnectionStringFor(Scenario.DbName);

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

        // Wire the connection (this scenario's DB) and the scenario theme via env vars BEFORE the host
        // boots — the WithWebHostBuilder-avoiding path. ApplyConfig sets ONLY the theme, never the
        // connection string, so this ordering is the single place the DB is bound. ResetThemeEnv first
        // clears any prior scenario's theme keys, so a direct `dotnet test` running both soak collections
        // serially in one process is order-independent (see SoakConfig.ResetThemeEnv).
        SoakConfig.ResetThemeEnv();
        SoakConfig.SetEnv("ConnectionStrings__Marten", connStr);
        Scenario.ApplyConfig();

        Host = await AlbaHost.For<Program>();
    }

    public Task DisposeAsync() => Host?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    // start-infra only provisions voidforge_test; each soak DB is separate, so create it if missing to
    // keep the run self-contained. Connect to the standard `postgres` maintenance database on the same
    // server (it always exists) and, because CREATE DATABASE cannot run inside a transaction, issue it
    // as a plain command.
    private static async Task EnsureDatabaseExistsAsync(string connStr, string targetDb)
    {
        var admin = new NpgsqlConnectionStringBuilder(connStr) { Database = "postgres" };
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
        // A database identifier cannot be parameterized, so escape it via NpgsqlCommandBuilder.QuoteIdentifier
        // (the "test"-guard on targetDb still applies).
        var quotedDb = new NpgsqlCommandBuilder().QuoteIdentifier(targetDb);
        create.CommandText = $"CREATE DATABASE {quotedDb};";
        await create.ExecuteNonQueryAsync();
    }
}
