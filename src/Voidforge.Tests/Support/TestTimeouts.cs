namespace Voidforge.Tests.Support;

/// <summary>
/// Canonical wall-clock poll cadence and deadlines for the integration suite.
/// These time out real HTTP polling — unrelated to the app's injected TimeProvider.
/// </summary>
public static class TestTimeouts
{
    /// <summary>Delay between successive polls.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Building/ship construction completing onto the planet.</summary>
    public static readonly TimeSpan Completion = TimeSpan.FromSeconds(20);

    /// <summary>Resource stock recovering after a spend, and multi-ship batches settling.</summary>
    public static readonly TimeSpan StockRecovery = TimeSpan.FromSeconds(30);

    /// <summary>Draining a multi-ship build queue.</summary>
    public static readonly TimeSpan QueueDrain = TimeSpan.FromSeconds(40);

    /// <summary>Real-scheduler fleet arrival (short hops).</summary>
    public static readonly TimeSpan Arrival = TimeSpan.FromSeconds(30);

    /// <summary>Full-loop end-to-end arrival (longest travel in the suite).</summary>
    public static readonly TimeSpan FullLoopArrival = TimeSpan.FromSeconds(60);
}
