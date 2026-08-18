using System.Diagnostics;
using Alba;
using Marten;
using Voidforge.Api.Domain;
using Voidforge.Tests.Support;

namespace Voidforge.SoakTests;

// Orchestrates the two-user scenario concurrently under one wall-clock window while a background loop
// captures the intermediate deposit series. After the scripts finish it keeps observing until the
// window closes, so the real scheduler has time to fire the completions the scripts triggered.
public sealed class SoakDriver
{
    private readonly IAlbaHost _host;
    private readonly IDocumentStore _store;

    public SoakDriver(IAlbaHost host, IDocumentStore store)
    {
        _host = host;
        _store = store;
    }

    public async Task<SoakDriverResult> RunAsync(TimeSpan window, Action<string>? log = null)
    {
        var recorder = new SoakRecorder();
        var stopwatch = Stopwatch.StartNew();
        var deadline = new Deadline(stopwatch, window);

        var regA = await _host.RegisterPlayer("SoakA_");
        var regB = await _host.RegisterPlayer("SoakB_");
        var homeA = await _host.GetPlanetById(regA, regA.HomeworldId);
        var homeB = await _host.GetPlanetById(regB, regB.HomeworldId);

        using var snapshotCts = new CancellationTokenSource();
        var snapshotLoop = CaptureDepositSnapshotsAsync(recorder, deadline, snapshotCts.Token);

        // A colonizes a second system and supplies it; B colonizes and recalls mid-transit. Each
        // excludes its own home system so the colonize legs reach out to another system.
        var scriptA = ScenarioScript.RunIndustrialistAsync(_host, recorder, regA, homeA.SolarSystemId, deadline);
        var scriptB = ScenarioScript.RunColonizerAsync(_host, recorder, regB, homeB.SolarSystemId, deadline);
        await Task.WhenAll(scriptA, scriptB);

        // Keep the window open so the real scheduler can fire the arrivals / ship completions the
        // scripts triggered, with the deposit-snapshot loop still running.
        while (!deadline.Reached)
        {
            await Task.Delay(SoakTimeouts.LegPause);
        }

        await snapshotCts.CancelAsync();
        await snapshotLoop;

        log?.Invoke(
            $"Driver recorded {recorder.Statuses.Count} raced status(es), {recorder.Snapshots.Count} deposit snapshot(s), {recorder.Events.Count} leg event(s).");
        return new SoakDriverResult(recorder.Statuses, recorder.Snapshots, recorder.Events);
    }

    private async Task CaptureDepositSnapshotsAsync(SoakRecorder recorder, Deadline deadline, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !deadline.Reached)
        {
            await CaptureOneAsync(recorder);
            await DelayQuietly(SoakTimeouts.IntermediateSnapshotInterval, token);
        }
    }

    private async Task CaptureOneAsync(SoakRecorder recorder)
    {
        var now = TimeProvider.System.GetUtcNow();
        await using var session = _store.LightweightSession();
        var planets = await session.Query<Planet>().ToListAsync();
        var deposits = planets.ToDictionary(p => p.Id, p => p.IronOreDeposit.GetCurrentValue(now));
        recorder.RecordSnapshot(new IntermediateSnapshot(now, deposits));
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (TaskCanceledException)
        {
            // Cancellation simply ends the snapshot loop; not an error.
        }
    }
}
