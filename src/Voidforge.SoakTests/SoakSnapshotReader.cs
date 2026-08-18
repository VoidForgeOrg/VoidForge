using System.Globalization;
using Marten;
using Npgsql;
using Voidforge.Api.Domain;

namespace Voidforge.SoakTests;

// The authoritative post-drain read. Resolves the DI-owned store, materializes every aggregate list
// at one fixed `now`, and reads the raw Wolverine dead-letter count for I3.
public static class SoakSnapshotReader
{
    public static async Task<WorldSnapshot> ReadAuthoritativeAsync(
        IDocumentStore store,
        DateTimeOffset now,
        string connectionString,
        IReadOnlyList<int> httpStatuses,
        IReadOnlyList<IntermediateSnapshot> depositSeries)
    {
        // Store is the DI singleton — a lightweight session over it, NEVER `await using` the store.
        await using var session = store.LightweightSession();
        var planets = await session.Query<Planet>().ToListAsync();
        var fleets = await session.Query<Fleet>().ToListAsync();
        var players = await session.Query<Player>().ToListAsync();

        var deadLetters = await CountDeadLettersAsync(connectionString);

        return new WorldSnapshot(planets, fleets, players, now, deadLetters, httpStatuses, depositSeries);
    }

    // Raw count of Wolverine's durable dead-letter queue (I3). The table lives in the Marten schema
    // (DatabaseSchemaName = "voidforge"). An undefined-table error means Wolverine has not provisioned
    // it, which is equivalent to zero dead letters.
    private static async Task<long> CountDeadLettersAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM voidforge.wolverine_dead_letters;";
        try
        {
            var scalar = await cmd.ExecuteScalarAsync();
            return scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal))
        {
            return 0;
        }
    }
}
