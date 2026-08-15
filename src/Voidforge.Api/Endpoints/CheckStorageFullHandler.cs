using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001). Validate-on-arrival: re-derive halts at the
// scheduled instant; a superseded message (rates changed since prediction, nothing at capacity)
// yields no events and no-ops. All domain logic lives in Planet.EvaluateStorageHalts.
public static class CheckStorageFullHandler
{
    public static async Task Handle(CheckStorageFull message, IDocumentSession session, IMessageBus bus)
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

        var halts = planet.EvaluateStorageHalts(message.PredictedAt);
        if (halts.Count > 0)
        {
            stream.AppendMany([.. halts]);
        }

        await session.SaveChangesAsync();

        // Reschedule from the FRESH post-commit aggregate. AppendMany does NOT re-apply events to the
        // in-memory stream.Aggregate, so PredictStorageDeadlines on `planet` would use PRE-halt rates.
        // Marten's inline snapshot is current after SaveChanges, so FetchLatest returns the just-written
        // aggregate whose rates already reflect RebaseRates from the applied halts.
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is null)
        {
            return;
        }

        await StorageHaltScheduling.ScheduleDeadlinesAsync(
            bus, message.PlanetId, updated.PredictStorageDeadlines(message.PredictedAt));
    }
}
