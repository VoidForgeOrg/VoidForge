using System.Globalization;
using Marten;
using Marten.Services;
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
        // All four reads (planets, fleets, players, dead-letter count) must observe ONE consistent
        // database snapshot, so run them inside a single REPEATABLE READ transaction. Postgres pins the
        // snapshot at the transaction's first statement, so the three Marten queries and the raw
        // dead-letter count all see the same instant. This avoids a false I8 duplicate that a
        // fleet/roster commit landing mid-read could otherwise produce.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        // Store is the DI singleton — open a lightweight session OVER the connection+transaction we own,
        // NEVER `await using` the store. SessionOptions.ForTransaction leaves OwnsConnection = false and
        // OwnsTransactionLifecycle = false, so Marten reads through them but disposes neither (we do).
        await using var session = store.LightweightSession(SessionOptions.ForTransaction(tx));
        var planets = await session.Query<Planet>().ToListAsync();
        var fleets = await session.Query<Fleet>().ToListAsync();
        var players = await session.Query<Player>().ToListAsync();

        var deadLetters = await CountDeadLettersAsync(conn, tx);

        return new WorldSnapshot(planets, fleets, players, now, deadLetters, httpStatuses, depositSeries);
    }

    // Raw count of Wolverine's durable dead-letter queue (I3), run on the SAME connection+transaction as
    // the aggregate reads so it observes the same snapshot. The table lives in the Marten schema
    // (DatabaseSchemaName = "voidforge"). An undefined-table error means Wolverine has not provisioned
    // it, which is equivalent to zero dead letters.
    private static async Task<long> CountDeadLettersAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
