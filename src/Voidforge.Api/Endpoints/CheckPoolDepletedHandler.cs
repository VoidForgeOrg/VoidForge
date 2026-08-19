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
        // CheckStorageFullHandler: AppendMany does not re-apply events to stream.Aggregate, so its rates
        // would still be the pre-halt ones.
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is null)
        {
            return;
        }

        if (events.Count > 0)
        {
            // A real depletion just halted the Drill(s) — a rate-changing mutation: oreInflow drops to 0 and
            // the still-Operational Refinery now drains the stored ore buffer. Arm the WHOLE downstream
            // cascade from the fresh aggregate, exactly as a mutation site (Place/Queue/Complete*) would, so
            // the depletion → drill-halt → refinery-InputStarved → build-halt chain fires in production
            // without a wall clock. Self-guarded and fan-out-free: after a real depletion
            // PredictDepletionDeadline is null (depletion is terminal, no CheckPoolDepleted re-arm), and the
            // storage/buffer predicts only schedule when genuinely filling/draining — so this arms the
            // now-draining buffer's CheckInputStarved and nothing spurious. Previously only CheckPoolDepleted
            // was rescheduled here, so the refinery-starvation leg was NEVER armed off the depletion path:
            // the refinery stayed Operational forever and EvaluateOreBufferEmptied never re-clamped the rate,
            // fabricating ingots from an empty buffer.
            await StorageHaltScheduling.ScheduleAllChecksAsync(bus, message.PlanetId, updated, message.PredictedAt);
        }
        else
        {
            // Superseded no-op (deposit not actually empty, or no operational Drill left): keep the chain
            // linear by rescheduling only the single next predicted empty instant.
            var deadline = updated.PredictDepletionDeadline(message.PredictedAt);
            if (deadline is not null)
            {
                await bus.ScheduleAsync(new CheckPoolDepleted(message.PlanetId, deadline.At), deadline.At);
            }
        }
    }
}
