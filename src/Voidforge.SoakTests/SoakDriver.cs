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
        var snapshotLoop = CaptureSnapshotsAsync(recorder, snapshotCts.Token);

        try
        {
            // A colonizes a second system and supplies it; B colonizes and recalls mid-transit. Each
            // excludes its own home system so the colonize legs reach out to another system.
            var scriptA = ScenarioScript.RunIndustrialistAsync(_host, recorder, regA, homeA.SolarSystemId, deadline);
            var scriptB = ScenarioScript.RunColonizerAsync(_host, recorder, regB, homeB.SolarSystemId, deadline);
            await Task.WhenAll(scriptA, scriptB);

            // Keep the window open so the real scheduler can fire the arrivals / ship completions the
            // scripts triggered, with the snapshot loop still running.
            while (!deadline.Reached)
            {
                await Task.Delay(SoakTimeouts.LegPause);
            }

            // Drain the scheduler with the snapshot loop STILL capturing, so a halt that a scheduled
            // event creates and then clears DURING the drain is still observed for Tier 3's O6 — a
            // loop stopped before drain, plus an already-cleared halt at the final read, would otherwise
            // hide a cascade that genuinely fired.
            await SchedulerQuiescence.DrainAsync(_store, log);
        }
        finally
        {
            // Always stop the snapshot loop first, even if a script or the wait loop threw, so it never
            // outlives this method (unobserved exception / queries during host disposal).
            await snapshotCts.CancelAsync();
            await snapshotLoop;
        }

        log?.Invoke(
            $"Driver recorded {recorder.Statuses.Count} raced status(es), {recorder.Snapshots.Count} snapshot(s), {recorder.Events.Count} leg event(s).");
        return new SoakDriverResult(recorder.Statuses, recorder.Snapshots, recorder.Events);
    }

    // Runs from just after registration until cancelled in RunAsync's finally — spanning BOTH the drive
    // window AND the scheduler drain, so no halt O6 relies on falls in an unobserved gap.
    private async Task CaptureSnapshotsAsync(SoakRecorder recorder, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
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

        // From the SAME query, capture any building halts live at this instant (no extra round-trip),
        // so Tier 3's O6 sees transient cascades the single post-drain snapshot could miss.
        var halts = planets
            .SelectMany(p => p.Buildings
                .Where(b => b.HaltReason is not null)
                .Select(b => new HaltObservation(p.Id, b.HaltReason!.Value)))
            .ToList();

        recorder.RecordSnapshot(new IntermediateSnapshot(now, deposits, halts));
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
