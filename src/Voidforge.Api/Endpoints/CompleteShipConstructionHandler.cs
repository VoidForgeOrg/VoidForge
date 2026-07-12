using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

public static class CompleteShipConstructionHandler
{
    public static async Task Handle(CompleteShipConstruction message, IDocumentSession session, IMessageBus bus)
    {
        var planet = await session.LoadAsync<Planet>(message.PlanetId);
        if (planet is null)
        {
            return;
        }

        var events = planet.CompleteShipBuild(message.BuildId, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        session.Events.Append(message.PlanetId, [.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, message.PlanetId, events);
        await session.SaveChangesAsync();
    }
}
