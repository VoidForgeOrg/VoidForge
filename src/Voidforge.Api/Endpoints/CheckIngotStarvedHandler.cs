using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001) for ingot-consumer starvation, cloned from
// CheckInputStarvedHandler. Validate-on-arrival: re-derive starvation at the scheduled instant; a
// superseded message (ingots returned since prediction, or the buffer not actually empty) yields no
// events and no-ops. All domain logic lives in Planet.EvaluateIngotStarvation.
public static class CheckIngotStarvedHandler
{
    public static async Task Handle(CheckIngotStarved message, IDocumentSession session, IMessageBus bus)
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

        var halts = planet.EvaluateIngotStarvation(message.PredictedAt);
        if (halts.Count > 0)
        {
            stream.AppendMany([.. halts]);
        }

        await session.SaveChangesAsync();

        // Reschedule from the FRESH post-commit aggregate (FetchLatest), same rationale as
        // CheckInputStarvedHandler: AppendMany does not re-apply events to stream.Aggregate. Pausing
        // the in-flight builds stops their ingot drain, so the buffer stops draining → IronIngot.Rate
        // >= 0 → PredictIngotBufferEmpty returns null (no reschedule — terminal). A superseded no-op
        // reschedules the single next predicted empty instant, keeping this per-kind chain linear.
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is null)
        {
            return;
        }

        var deadline = updated.PredictIngotBufferEmpty(message.PredictedAt);
        if (deadline is not null)
        {
            await bus.ScheduleAsync(new CheckIngotStarved(message.PlanetId, deadline.At), deadline.At);
        }
    }
}
