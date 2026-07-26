using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Endpoints;

public static class CompleteFleetArrivalHandler
{
    public static async Task Handle(CompleteFleetArrival message, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Fleet>(message.FleetId);
        var fleet = stream.Aggregate;
        if (fleet is null)
        {
            return;
        }

        var events = fleet.Arrive(message.ArrivesAt);
        if (events.Count == 0)
        {
            return;   // stale or superseded message (ADR 0001 validate-on-arrival)
        }

        stream.AppendMany([.. events]);
        await session.SaveChangesAsync();
    }
}
