using System.Diagnostics;
using Marten;
using Voidforge.Api.Domain;

namespace Voidforge.SoakTests;

// Aggregate-quiesce + settle-cap drain of the REAL Wolverine scheduler (no coupling to Wolverine's
// internal envelope tables). Polls world state until nothing is overdue (or a hard cap elapses), then
// waits a fixed settle window before the authoritative snapshot is taken.
public static class SchedulerQuiescence
{
    // Overdue tolerance: the ~5s durability poll plus the #39 ConcurrencyException retry span (~7s),
    // times a safety factor. A completion later than this is genuinely stuck, not merely in flight.
    private static readonly TimeSpan _overdueMargin = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _hardCap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _settle = TimeSpan.FromSeconds(15);

    public static async Task DrainAsync(IDocumentStore store, Action<string>? log = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var quiescent = false;
        while (stopwatch.Elapsed < _hardCap)
        {
            if (!await AnythingOverdueAsync(store))
            {
                quiescent = true;
                break;
            }

            await Task.Delay(_pollInterval);
        }

        var elapsed = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        log?.Invoke(quiescent
            ? $"Scheduler quiesced after {elapsed}s; settling {_settle.TotalSeconds}s."
            : $"Drain hard cap ({_hardCap.TotalSeconds}s) reached before quiescence; settling {_settle.TotalSeconds}s.");

        await Task.Delay(_settle);
    }

    private static async Task<bool> AnythingOverdueAsync(IDocumentStore store)
    {
        var cutoff = TimeProvider.System.GetUtcNow() - _overdueMargin;
        await using var session = store.LightweightSession();
        var planets = await session.Query<Planet>().ToListAsync();
        var fleets = await session.Query<Fleet>().ToListAsync();

        var buildingOverdue = planets.Any(p => p.Buildings.Any(b => IsBuildingOverdue(b, cutoff)));
        var shipOverdue = planets.Any(p => p.ShipQueue.Any(b => IsShipBuildOverdue(b, cutoff)));
        var fleetOverdue = fleets.Any(f => IsFleetOverdue(f, cutoff));

        return buildingOverdue || shipOverdue || fleetOverdue;
    }

    // A ConstructionHalted build is a MODELED (ingot-starved) state, not "stuck", so only
    // UnderConstruction counts as overdue.
    private static bool IsBuildingOverdue(BuildingSlot slot, DateTimeOffset cutoff) =>
        slot.Status == BuildingStatus.UnderConstruction && slot.CompletesAt is { } at && at < cutoff;

    // Queued builds wait on shipyard capacity (not the scheduler) and Halted builds are ingot-starved;
    // only an Active build past its deadline is genuinely overdue.
    private static bool IsShipBuildOverdue(ShipBuild build, DateTimeOffset cutoff) =>
        build.Status == ShipBuildStatus.Active && build.CompletesAt is { } at && at < cutoff;

    private static bool IsFleetOverdue(Fleet fleet, DateTimeOffset cutoff) =>
        fleet.Status == FleetStatus.InTransit && fleet.ArrivesAt is { } at && at < cutoff;
}
