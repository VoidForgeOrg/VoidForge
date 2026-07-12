using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Shared by the ship endpoints and the two completion handlers: schedule a durable completion
// for every ship build that just started (immediate enqueue, auto-start on completion/cancel,
// or a completing Shipyard raising capacity).
public static class ShipConstructionScheduling
{
    public static async Task ScheduleStartedBuildsAsync(
        IMessageBus bus, Guid planetId, IEnumerable<object> events)
    {
        foreach (var started in events.OfType<ShipConstructionStarted>())
        {
            await bus.ScheduleAsync(
                new CompleteShipConstruction(planetId, started.BuildId, started.CompletesAt),
                started.CompletesAt);
        }
    }
}
