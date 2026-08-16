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

        var events = planet.EvaluateInputStarvation(message.PredictedAt);

        // No starvation halt means either a superseded check (ore returned) or a Refinery running at
        // REDUCED throughput (positive-but-insufficient inflow). In the reduced-throughput case the buffer
        // emptying still requires a rate rebase so EffectiveOreConsumption clamps to the sustainable inflow
        // and ingot production stops over-counting (#70). EvaluateOreBufferEmptied returns that single
        // composition-neutral rebase when the buffer is empty and still draining, [] otherwise.
        if (events.Count == 0)
        {
            events = planet.EvaluateOreBufferEmptied(message.PredictedAt);
        }

        if (events.Count > 0)
        {
            stream.AppendMany([.. events]);
        }

        await session.SaveChangesAsync();

        // Reschedule from the FRESH post-commit aggregate (FetchLatest), same rationale as
        // CheckPoolDepletedHandler: AppendMany does not re-apply events to stream.Aggregate. Whether the
        // Refinery halted (starved) or the rates rebased (reduced throughput), consumption now matches the
        // available ore, so the buffer stops draining → IronOre.Rate >= 0 → PredictBufferEmpty returns null
        // (no reschedule — terminal). A superseded no-op reschedules the single next predicted empty
        // instant, keeping the chain linear.
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
