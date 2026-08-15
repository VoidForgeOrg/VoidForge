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
}
