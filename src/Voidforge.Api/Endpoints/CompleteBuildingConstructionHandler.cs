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

        // A completing Drill restores ore inflow, which can un-starve a Refinery halted InputStarved
        // (#70). Resume is composition-driven, so it must ride the SAME commit as the completion (its
        // RebaseRates restores the Refinery's throughput atomically). AppendMany does NOT re-apply
        // events to stream.Aggregate, so apply the completion(s) in-memory first, otherwise
        // CurrentOreInflow() would still read the pre-completion (zero-inflow) composition and the
        // resume would be missed. See ApplyCompletionsForResumeEvaluation for why the double-apply is
        // safe. NOTE: the ore-buffer restore paths (cargo delivery — transport arrival and the manual
        // unload endpoint) are a follow-up; a completing Drill is the covered case here.
        ApplyCompletionsForResumeEvaluation(planet, events);
        var resumes = planet.EvaluateInputStarvationResumes(message.CompletesAt);
        if (resumes.Count > 0)
        {
            stream.AppendMany([.. resumes]);
        }

        await session.SaveChangesAsync();

        // A newly Operational producer changes production rates (and thus every cascade deadline).
        // Reschedule from the FRESH post-commit aggregate — the stale `planet` above was mutated only
        // for the in-commit resume evaluation, so read the authoritative post-commit state (#69/#70).
        var updated = await session.Events.FetchLatest<Planet>(message.PlanetId);
        if (updated is not null)
        {
            await StorageHaltScheduling.ScheduleAllChecksAsync(
                bus, message.PlanetId, updated, message.CompletesAt);
        }
    }

    // Apply just the completion(s) to the in-memory aggregate so EvaluateInputStarvationResumes reads
    // the POST-completion ore inflow (a newly Operational Drill). BuildingCompleted's Apply is purely
    // composition-based and idempotent — it sets the slot Operational (already is, after this) and
    // RebaseRates from the same building set — so Marten re-applying the appended completion onto this
    // identity-mapped instance at SaveChanges lands on the identical state. Only BuildingCompleted
    // feeds ore inflow; other completion side effects (auto-started ship builds) don't gate a
    // Refinery's input, so they're left for Marten's commit-time apply.
    private static void ApplyCompletionsForResumeEvaluation(Planet planet, IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            if (e is BuildingCompleted completed)
            {
                planet.Apply(completed);
            }
        }
    }
}
