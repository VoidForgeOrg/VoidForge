using System.Globalization;

namespace Voidforge.SoakTests;

// The §8.2 "depletion + ingot-storage-full" soak theme, expressed as env-var overrides applied before
// the real host boots (ASP.NET Core maps `__` to the `:` config hierarchy). Mirrors AppFixture's
// env-var-before-boot approach so it uses the WithWebHostBuilder-avoiding path.
public static class SoakConfig
{
    // The DB name contains "test" so SoakHostFixture's drop-schema safety guard passes unmodified.
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=voidforge_soak_test;Username=postgres;Password=voidforge_dev";

    private const int _defaultWindowSeconds = 120;

    // Overridable via the dedicated VOIDFORGE_SOAK_CONNECTION_STRING env var (read first, soak default
    // second). Deliberately NOT the shared ConnectionStrings__Marten host key, so a stray host-level
    // Marten connection string can never be picked up here and have its schema dropped.
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("VOIDFORGE_SOAK_CONNECTION_STRING") ?? DefaultConnectionString;

    // Bounded soak window. 120s exercises the whole loop; the §8.2 depletion + ingot-storage-full
    // cascades fire at ~170-200s, so OBSERVING them (a Tier-3 follow-up) needs SOAK_WINDOW_SECONDS=300.
    // The Tier-1 invariants hold at every instant regardless of window, so 120s is fine for the skeleton.
    public static int WindowSeconds => ReadWindowSeconds();

    // Emit mode: when set, the test logs its computed SoakAggregates as one grep-able marker line
    // (SoakBaselineEmitter) so the N-run blessing envelope can be built from machine-readable output.
    public static bool EmitBaseline => ReadFlag("SOAK_EMIT_BASELINE");

    public static void ApplyEnvironmentOverrides()
    {
        // Set the connection string before AlbaHost.For<Program>() so the host binds it via `__` -> `:`
        // (the WithWebHostBuilder-avoiding path AppFixture documents to dodge a .NET 9 disposal race).
        SetEnv("ConnectionStrings__Marten", ConnectionString);

        // Rich-economy world: a finite ore deposit and an ingot store both sized so depletion and
        // ingot-storage-full can be reached within a few-minute window.
        SetEnv("WorldGeneration__IronOrePool", "4000");
        SetEnv("WorldGeneration__IronIngotStorageCapacity", "2500");
        SetEnv("WorldGeneration__StartingIronOre", "2000");
        SetEnv("WorldGeneration__StartingIronIngots", "800");
        SetEnv("WorldGeneration__SolarSystemCount", "40");

        // Soak-scale construction durations and ship costs/speeds: fast enough to complete within the
        // window, slow enough that the real scheduler genuinely spreads completions across wall-clock.
        SetEnv("Balance__Drill__BuildDurationSeconds", "20");
        SetEnv("Balance__Refinery__BuildDurationSeconds", "20");
        SetEnv("Balance__Generator__BuildDurationSeconds", "20");
        SetEnv("Balance__Shipyard__BuildDurationSeconds", "20");
        SetEnv("Balance__ColonyShip__BuildDurationSeconds", "15");
        SetEnv("Balance__CargoVessel__BuildDurationSeconds", "15");
        SetEnv("Balance__ColonyShip__IngotCost", "60");
        SetEnv("Balance__CargoVessel__IngotCost", "60");
        SetEnv("Balance__Ships__ColonyShip__SpeedPerSecond", "100");
        SetEnv("Balance__Ships__CargoVessel__SpeedPerSecond", "100");

        // Economy is deliberately left unset: it binds from appsettings.json (10/s drill, 5/s refinery,
        // 1:2 ratio), which is exactly what the §4.2 depletion math assumes.

        PinLogLevels();
    }

    // Copied from AppFixture.PinTestLogLevels: silence the per-SQL Debug/Info logging the Development
    // environment defaults switch on, so a long soak run does not bury real failures in log noise.
    private static void PinLogLevels()
    {
        SetEnv("Logging__LogLevel__Default", "Information");
        SetEnv("Logging__LogLevel__Marten", "Warning");
        SetEnv("Logging__LogLevel__Wolverine", "Warning");
        SetEnv("Logging__LogLevel__Npgsql", "Warning");
    }

    private static void SetEnv(string key, string value) => Environment.SetEnvironmentVariable(key, value);

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
