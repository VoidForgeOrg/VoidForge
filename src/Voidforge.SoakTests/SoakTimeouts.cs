namespace Voidforge.SoakTests;

// Soak-scale wall-clock cadences and deadlines — deliberately longer than the integration suite's
// TestTimeouts because the soak drives real, contended, config-heavy economy dynamics.
public static class SoakTimeouts
{
    // Real pause between a user script's legs so its actions spread across the run's polls.
    public static readonly TimeSpan LegPause = TimeSpan.FromSeconds(5);

    // Waiting for a freshly-placed Shipyard to finish its (soak-config, 20s) construction.
    public static readonly TimeSpan ShipyardOperational = TimeSpan.FromSeconds(40);

    // A real-scheduler fleet arrival: cross-system transit at the soak ship speed plus the ~5s
    // durability poll lag and the #39 retry span.
    public static readonly TimeSpan FleetArrival = TimeSpan.FromSeconds(90);

    // Cadence of the during-run intermediate deposit snapshots (the I11 monotonicity series).
    public static readonly TimeSpan IntermediateSnapshotInterval = TimeSpan.FromSeconds(10);
}
