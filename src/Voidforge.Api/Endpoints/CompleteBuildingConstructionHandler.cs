using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001). All domain logic lives in the pure
// Planet.CompleteBuilding; a stale/superseded message yields no events and no-ops.
public static class CompleteBuildingConstructionHandler
{
    public static async Task Handle(CompleteBuildingConstruction message, IDocumentSession session, IMessageBus bus)
    {
        // FetchForWriting loads the aggregate and arms Marten's optimistic-concurrency guard from the
        // fetched stream version; a racing append then fails on SaveChanges with a ConcurrencyException
        // (retried via the Wolverine policy in Program.cs) rather than colliding at the DB (#39).
        var stream = await session.Events.FetchForWriting<Planet>(message.PlanetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return;
        }

        var events = planet.CompleteBuilding(message.SlotIndex, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany([.. events]);
        // A completing Shipyard can auto-start queued ship builds — schedule their completions.
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, message.PlanetId, events);
        await session.SaveChangesAsync();

        // A newly Operational producer changes production rates (and thus fill deadlines). Reschedule
        // from the FRESH post-commit aggregate — AppendMany does not re-apply events to stream.Aggregate,
        // so PredictStorageDeadlines on the stale `planet` would use pre-completion rates (#69).
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is not null)
        {
            await StorageHaltScheduling.ScheduleDeadlinesAsync(
                bus, message.PlanetId, updated.PredictStorageDeadlines(message.CompletesAt));
        }
    }
}
