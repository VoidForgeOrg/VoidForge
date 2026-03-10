using Alba;
using Npgsql;
using Xunit;

namespace Voidforge.Tests;

public sealed class AppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__Marten")
            ?? "Host=localhost;Port=5432;Database=voidforge_test;Username=postgres;Password=voidforge_dev";

        // ASP.NET Core maps env vars with __ to : in the config hierarchy.
        // Set before AlbaHost.For<Program>() so the host picks it up automatically.
        // Using the env var path avoids AlbaHost.For's WithWebHostBuilder overload,
        // which triggers a service provider disposal race with RunJasperFxCommands in .NET 9.
        Environment.SetEnvironmentVariable("ConnectionStrings__Marten", connStr);

        // Safety check: refuse to drop schema on a non-test database.
        var builder = new NpgsqlConnectionStringBuilder(connStr);
        if (builder.Database is not { } db || !db.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to reset database '{builder.Database}'. Test connection string must target a database containing 'test' in its name.");
        }

        // Drop the schema before the host starts so the WorldSeeder re-seeds fresh data.
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS voidforge CASCADE;";
        await cmd.ExecuteNonQueryAsync();

        Host = await AlbaHost.For<Program>();
    }

    public Task DisposeAsync() => Host?.DisposeAsync().AsTask() ?? Task.CompletedTask;
}
