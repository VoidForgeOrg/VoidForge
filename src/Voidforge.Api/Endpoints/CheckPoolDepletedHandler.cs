using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001) for ore-deposit depletion, cloned from
// CheckStorageFullHandler. Validate-on-arrival: re-derive depletion at the scheduled instant; a
// superseded message (drills removed/halted since prediction, deposit not actually empty) yields no
// events and no-ops. All domain logic lives in Planet.EvaluateDepletion.
public static class CheckPoolDepletedHandler
{
    public static async Task Handle(CheckPoolDepleted message, IDocumentSession session, IMessageBus bus)
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

        var events = planet.EvaluateDepletion(message.PredictedAt);
        if (events.Count > 0)
        {
            stream.AppendMany([.. events]);
        }

        await session.SaveChangesAsync();

        // Reschedule from the FRESH post-commit aggregate (FetchLatest), same rationale as
        // CheckStorageFullHandler: AppendMany does not re-apply events to stream.Aggregate. After a
        // real depletion every Drill is Halted → oreInflow 0 → the deposit's drain Rate 0 →
        // PredictDepletionDeadline returns null (no reschedule — depletion is terminal). A superseded
        // no-op reschedules the single next predicted empty instant, keeping the chain linear.
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is null)
        {
            return;
        }

        var deadline = updated.PredictDepletionDeadline(message.PredictedAt);
        if (deadline is not null)
        {
            await bus.ScheduleAsync(new CheckPoolDepleted(message.PlanetId, deadline.At), deadline.At);
        }
    }
}
