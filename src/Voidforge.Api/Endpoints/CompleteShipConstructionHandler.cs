using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

public static class CompleteShipConstructionHandler
{
    public static async Task Handle(CompleteShipConstruction message, IDocumentSession session, IMessageBus bus)
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

        var events = planet.CompleteShipBuild(message.BuildId, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany([.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, message.PlanetId, events);
        await session.SaveChangesAsync();

        // A completed build drops its ingot drain (and any auto-started build changes it again),
        // shifting the ingot fill deadline. Reschedule from the FRESH post-commit aggregate —
        // AppendMany does not re-apply events to stream.Aggregate, so the stale `planet` has old rates (#69).
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is not null)
        {
            await StorageHaltScheduling.ScheduleAllChecksAsync(
                bus, message.PlanetId, updated, message.CompletesAt);
        }
    }
}
