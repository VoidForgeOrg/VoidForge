using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001) for refinery ore-starvation, cloned from
// CheckPoolDepletedHandler. Validate-on-arrival: re-derive starvation at the scheduled instant; a
// superseded message (ore returned since prediction, or the buffer not actually empty) yields no
// events and no-ops. All domain logic lives in Planet.EvaluateInputStarvation.
public static class CheckInputStarvedHandler
{
    public static async Task Handle(CheckInputStarved message, IDocumentSession session, IMessageBus bus)
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

        var halts = planet.EvaluateInputStarvation(message.PredictedAt);
        if (halts.Count > 0)
        {
            stream.AppendMany([.. halts]);
        }

        await session.SaveChangesAsync();

        // Reschedule from the FRESH post-commit aggregate (FetchLatest), same rationale as
        // CheckPoolDepletedHandler: AppendMany does not re-apply events to stream.Aggregate. A starved
        // Refinery halting stops its consumption, so the buffer stops draining → IronOre.Rate >= 0 →
        // PredictBufferEmpty returns null (no reschedule — terminal). A superseded no-op reschedules
        // the single next predicted empty instant, keeping the chain linear.
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is null)
        {
            return;
        }

        var deadline = updated.PredictBufferEmpty(message.PredictedAt);
        if (deadline is not null)
        {
            await bus.ScheduleAsync(new CheckInputStarved(message.PlanetId, deadline.At), deadline.At);
        }
    }
}
