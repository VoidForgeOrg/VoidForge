using System.Diagnostics;
using Alba;
using Marten;
using Voidforge.Api.Domain;

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

    public async Task<SoakDriverResult> RunAsync(SoakScenarioBody body, TimeSpan window, Action<string>? log = null)
    {
        var recorder = new SoakRecorder();
        var stopwatch = Stopwatch.StartNew();
        var deadline = new Deadline(stopwatch, window);

        using var snapshotCts = new CancellationTokenSource();
        var snapshotLoop = CaptureSnapshotsAsync(recorder, snapshotCts.Token);

        try
        {
            // The scenario body owns player registration + scripting (scenarios differ in player count);
            // the driver owns the reusable orchestration around it. The snapshot loop is already running,
            // so it tolerates the pre-registration world (only the seeded uncolonized planets exist yet).
            await body(_host, recorder, deadline);

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
