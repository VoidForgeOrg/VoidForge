using System.Globalization;
using Npgsql;

namespace Voidforge.SoakTests;

// Shared soak plumbing: the run window, the emit-mode flag, per-scenario connection-string composition,
// and the env-var helpers scenarios use to express their theme. The world/balance THEME itself lives on
// each scenario (SoakScenario.ApplyConfig) — this class only carries what is common to every scenario.
// Env vars are set before AlbaHost.For<Program>() so the host binds them via `__` -> `:` (the
// WithWebHostBuilder-avoiding path AppFixture documents to dodge a .NET 9 disposal race).
public static class SoakConfig
{
    // Base template: host/port/credentials come from VOIDFORGE_SOAK_CONNECTION_STRING when set (CI passes
    // it), otherwise this local default. The DATABASE is always overridden per scenario (ConnectionStringFor),
    // so a scenario can never accidentally inherit another scenario's — or the shared voidforge_test — DB.
    // The default DB name contains "test" so the drop-schema safety guard passes unmodified.
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=voidforge_soak_test;Username=postgres;Password=voidforge_dev";

    private const int _defaultWindowSeconds = 120;

    // Bounded soak window. 120s exercises the whole loop; the two-user depletion + ingot-storage-full
    // cascades fire at ~170-200s, so observing THEM needs SOAK_WINDOW_SECONDS=300. Tier-1 invariants hold
    // at every instant regardless of window.
    public static int WindowSeconds => ReadWindowSeconds();

    // Emit mode: when set, the run logs its computed SoakAggregates as one grep-able marker line
    // (SoakBaselineEmitter) so the N-run blessing envelope can be built from machine-readable output.
    public static bool EmitBaseline => ReadFlag("SOAK_EMIT_BASELINE");

    // The connection string for one scenario's DB: take the base template's host/port/credentials and
    // override only the database name. Keeps every scenario on one Postgres server, isolated by DB.
    public static string ConnectionStringFor(string dbName)
    {
        var baseConn = Environment.GetEnvironmentVariable("VOIDFORGE_SOAK_CONNECTION_STRING") ?? DefaultConnectionString;
        var builder = new NpgsqlConnectionStringBuilder(baseConn) { Database = dbName };
        return builder.ConnectionString;
    }

    public static void SetEnv(string key, string value) => Environment.SetEnvironmentVariable(key, value);

    // Scenario themes set keys under these config prefixes (each scenario's ApplyConfig). Env vars are
    // process-global, so a direct `dotnet test` running multiple soak collections serially in ONE process
    // would otherwise let a scenario inherit the PRIOR scenario's theme keys (e.g. two-user's IronOrePool
    // leaking into input-starvation), making results depend on scenario order. Clearing these prefixes
    // before each in-process boot gives every scenario a clean slate. (The matrix runner already isolates
    // via separate processes; this makes the single-process path order-independent too.) The connection
    // string and log levels are re-set on every boot, so they never bleed and are not cleared here.
    private static readonly string[] _themePrefixes = ["WorldGeneration__", "Balance__"];

    public static void ResetThemeEnv()
    {
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
        {
            if (_themePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    // Silence the per-SQL Debug/Info logging the Development environment defaults switch on, so a long soak
    // run does not bury real failures in log noise. Every scenario's ApplyConfig calls this.
    public static void PinLogLevels()
    {
        SetEnv("Logging__LogLevel__Default", "Information");
        SetEnv("Logging__LogLevel__Marten", "Warning");
        SetEnv("Logging__LogLevel__Wolverine", "Warning");
        SetEnv("Logging__LogLevel__Npgsql", "Warning");
    }

    private static int ReadWindowSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("SOAK_WINDOW_SECONDS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : _defaultWindowSeconds;
    }

    // A plain on/off env flag: "1" or "true" (case-insensitive) enables it; anything else (unset included)
    // is off. Deliberately not culture- or number-parsed — it is a switch, not a value.
    private static bool ReadFlag(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}
