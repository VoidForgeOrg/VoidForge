using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;

namespace Voidforge.Api.Endpoints;

// Reschedules a fresh completion for every build that just resumed on an ingot-return commit (#83).
// When a build halts, its original CompleteBuildingConstruction/CompleteShipConstruction message is
// left to fire and no-op (validate-on-arrival, ADR 0001) — so a resumed build has NO live completion
// and needs a new one at its recomputed CompletesAt. The `planet` MUST be the post-commit FetchLatest'd
// aggregate: its resumed slots/builds already carry Apply(ConstructionResumed)/Apply(ShipBuildResumed)'s
// pushed-out CompletesAt (resumeAt + remaining). Reading the stale pre-commit aggregate would reschedule
// at the wrong (un-pushed, or still-halted) instant.
public static class ResumeScheduling
{
    public static async Task ScheduleResumedBuildsAsync(
        IMessageBus bus, Guid planetId, Planet planet, IEnumerable<object> resumeEvents)
    {
        foreach (var resumed in resumeEvents)
        {
            if (resumed is ConstructionResumed construction
                && planet.Buildings[construction.SlotIndex].CompletesAt is { } buildingAt)
            {
                await bus.ScheduleAsync(
                    new CompleteBuildingConstruction(planetId, construction.SlotIndex, buildingAt), buildingAt);
            }
            else if (resumed is ShipBuildResumed ship
                && planet.ShipQueue.FirstOrDefault(b => b.Id == ship.BuildId)?.CompletesAt is { } shipAt)
            {
                await bus.ScheduleAsync(
                    new CompleteShipConstruction(planetId, ship.BuildId, shipAt), shipAt);
            }
        }
    }
}
