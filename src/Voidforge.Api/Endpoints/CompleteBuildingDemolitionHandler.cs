using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001) for demolition's timed second step. All domain
// logic lives in the pure Planet.CompleteDemolition; a stale/superseded message yields no events and
// no-ops. No resume hook is wired: per plan decision 4 there is nothing a demolition resumes in #72's
// world (deferred to #83). The immediate shutdown already happened at StartDemolition; this only frees
// the slot, so the only follow-up is rescheduling cascade checks from the fresh post-commit state.
public static class CompleteBuildingDemolitionHandler
{
    public static async Task Handle(CompleteBuildingDemolition message, IDocumentSession session, IMessageBus bus)
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

        var events = planet.CompleteDemolition(message.SlotIndex, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany([.. events]);
        await session.SaveChangesAsync();

        // The freed slot leaves the composition unchanged energy-wise (the building already stopped at
        // StartDemolition), but reschedule all cascade checks from the FRESH post-commit aggregate for
        // consistency with every other mutation site (#69/#70).
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is not null)
        {
            await StorageHaltScheduling.ScheduleAllChecksAsync(
                bus, message.PlanetId, updated, message.CompletesAt);
        }
    }
}
