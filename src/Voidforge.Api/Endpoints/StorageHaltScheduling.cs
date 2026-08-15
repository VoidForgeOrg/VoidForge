using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Shared by the storage-full handler and the four composition/rate-changing commit sites
// (BuildingEndpoints.Place, ShipEndpoints.Queue, the two completion handlers): schedule a durable
// CheckStorageFull at each pool's predicted fill instant. Superseded checks fire and no-op
// (validate-on-arrival, ADR 0001) — schedules are never cancelled.
public static class StorageHaltScheduling
{
    public static async Task ScheduleDeadlinesAsync(
        IMessageBus bus, Guid planetId, IReadOnlyList<StorageDeadline> deadlines)
    {
        foreach (var deadline in deadlines)
        {
            await bus.ScheduleAsync(
                new CheckStorageFull(planetId, deadline.Resource, deadline.At),
                deadline.At);
        }
    }

    // Unified scheduling for every rate-changing MUTATION site (#70): arm ALL FOUR cascade checks
    // from one fresh post-commit planet — storage-full (CheckStorageFull, per pool), ore-deposit
    // depletion (CheckPoolDepleted, when the deposit is draining), refinery buffer-empty
    // (CheckInputStarved, when the ore buffer is draining) and ingot buffer-empty (CheckIngotStarved,
    // when in-flight builds drain the ingot buffer, #83). The per-kind ScheduleDeadlinesAsync above
    // stays for the check handlers' OWN linear self-reschedule; only the mutation sites call this so
    // the depletion → drill-halt → refinery-starvation → build-halt cascade fires in production
    // without a wall clock.
    public static async Task ScheduleAllChecksAsync(
        IMessageBus bus, Guid planetId, Planet planet, DateTimeOffset now)
    {
        await ScheduleDeadlinesAsync(bus, planetId, planet.PredictStorageDeadlines(now));

        var depletion = planet.PredictDepletionDeadline(now);
        if (depletion is not null)
        {
            await bus.ScheduleAsync(new CheckPoolDepleted(planetId, depletion.At), depletion.At);
        }

        var bufferEmpty = planet.PredictBufferEmpty(now);
        if (bufferEmpty is not null)
        {
            await bus.ScheduleAsync(new CheckInputStarved(planetId, bufferEmpty.At), bufferEmpty.At);
        }

        var ingotEmpty = planet.PredictIngotBufferEmpty(now);
        if (ingotEmpty is not null)
        {
            await bus.ScheduleAsync(new CheckIngotStarved(planetId, ingotEmpty.At), ingotEmpty.At);
        }
    }
}
