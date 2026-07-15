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
    }
}
