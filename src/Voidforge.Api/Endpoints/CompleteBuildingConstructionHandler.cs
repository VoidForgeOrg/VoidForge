using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Thin, idempotent durable-message handler (ADR 0001). All domain logic lives in the pure
// Planet.CompleteBuilding; a stale/superseded message yields no events and no-ops.
public static class CompleteBuildingConstructionHandler
{
    public static async Task Handle(CompleteBuildingConstruction message, IDocumentSession session, IMessageBus bus)
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

        var events = planet.CompleteBuilding(message.SlotIndex, message.CompletesAt);
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany([.. events]);
        // A completing Shipyard can auto-start queued ship builds — schedule their completions.
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, message.PlanetId, events);

        // Composition-driven resume cascade, riding the SAME commit as the completion (engine.md
        // scenario 2's tail): a completing Drill restores ore inflow → a Refinery halted InputStarved
        // resumes (#70) → ingot production returns → the ingot-starved in-flight builds resume (#83).
        // AppendMany does NOT re-apply to stream.Aggregate, so each tier is applied in-memory before the
        // next evaluates, else that tier reads the pre-resume composition. ApplyForResumeEvaluation
        // documents why the in-memory apply is safe (and why ingot resumes are excluded from it). The
        // ingot cargo-delivery resume path is deferred, symmetric to the already-unwired ore-buffer one.
        ApplyForResumeEvaluation(planet, events);
        var refineryResumes = planet.EvaluateInputStarvationResumes(message.CompletesAt);
        if (refineryResumes.Count > 0)
        {
            // Apply the Refinery resumes in-memory too so IngotProduction reflects the resumed Refinery
            // before EvaluateIngotStarvationResumes runs.
            stream.AppendMany([.. refineryResumes]);
            ApplyForResumeEvaluation(planet, refineryResumes);
        }

        var ingotResumes = planet.EvaluateIngotStarvationResumes(message.CompletesAt);
        if (ingotResumes.Count > 0)
        {
            stream.AppendMany([.. ingotResumes]);
        }

        await session.SaveChangesAsync();

        // A newly Operational producer changes production rates (and thus every cascade deadline), and a
        // resumed build needs a fresh completion at its recomputed CompletesAt. Reschedule from the FRESH
        // post-commit aggregate whose resumed slots/builds carry the pushed-out CompletesAt (#69/#70/#83).
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is not null)
        {
            await ResumeScheduling.ScheduleResumedBuildsAsync(bus, message.PlanetId, updated, ingotResumes);
            await StorageHaltScheduling.ScheduleAllChecksAsync(
                bus, message.PlanetId, updated, message.CompletesAt);
        }
    }

    // Applies the composition-changing events of a resume tier to the in-memory aggregate so the NEXT
    // tier's evaluator reads the post-change rates: BuildingCompleted (a newly Operational Drill →
    // CurrentOreInflow) feeds the Refinery-resume evaluation, and BuildingResumed (a newly Operational
    // Refinery → IngotProduction) feeds the ingot-consumer-resume evaluation. Both Applys are absolute
    // composition-based idempotent (set the slot Operational + RebaseRates from the same building set),
    // so Marten re-applying the appended event onto this identity-mapped instance at SaveChanges lands
    // on the identical state.
    //
    // The ingot-consumer resumes (ConstructionResumed/ShipBuildResumed) are DELIBERATELY not applied
    // here: their Apply recomputes CompletesAt from HaltedAt and then CLEARS HaltedAt, so a second
    // (commit-time) re-apply would read a null HaltedAt and throw — they are NOT idempotent under the
    // double-apply. They are appended and left for Marten's single commit-time apply; their recomputed
    // CompletesAt is read post-commit via FetchLatest (ResumeScheduling) for rescheduling. Nothing after
    // EvaluateIngotStarvationResumes reads the in-memory aggregate, so not applying them is harmless.
    // Auto-started ship builds (ShipConstructionStarted) also gate nothing here, so they're skipped too.
    private static void ApplyForResumeEvaluation(Planet planet, IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case BuildingCompleted completed:
                    planet.Apply(completed);
                    break;
                case BuildingResumed resumed:
                    planet.Apply(resumed);
                    break;
            }
        }
    }
}
