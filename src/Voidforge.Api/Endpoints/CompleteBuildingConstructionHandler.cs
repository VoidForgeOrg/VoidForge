using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001). All domain logic lives in the pure
// Planet.CompleteBuilding; a stale/superseded message yields no events and no-ops.
public static class CompleteBuildingConstructionHandler
{
    public static async Task Handle(CompleteBuildingConstruction message, IDocumentSession session)
    {
        var planet = await session.LoadAsync<Planet>(message.PlanetId);
        if (planet is null)
        {
            return;
        }

        var events = planet.CompleteBuilding(message.SlotIndex, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        session.Events.Append(message.PlanetId, [.. events]);
        await session.SaveChangesAsync();
    }
}
