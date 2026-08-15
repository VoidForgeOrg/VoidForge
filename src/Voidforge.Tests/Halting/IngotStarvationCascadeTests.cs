using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Wolverine;
using Xunit;

namespace Voidforge.Tests.Halting;

// Ingot-starvation scheduled-check e2e (#83, Task 3): the ingot-consumer mirror of DepletionCascadeTests.
// A planet driven into the clean zero-ingot state (no operational refinery producing ingots AND an empty
// IronIngot buffer) has its in-flight consumers — an UnderConstruction building and an Active ship build —
// paused by CheckIngotStarvedHandler. Driven deterministically by seeding the starved state onto the
// homeworld stream (BuildingHalted refinery + oversized CargoLoadedFromStorage pins the buffer to 0,
// mirroring StorageHaltingTests) and invoking the handler DIRECTLY at the seed instant — no wall-clock waits.
[Collection(IntegrationCollection.Name)]
public sealed class IngotStarvationCascadeTests
{
    private readonly IAlbaHost _host;

    public IngotStarvationCascadeTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task IngotStarvationHaltsInFlightBuildAndShipBuildWhenBufferEmpties()
    {
        var registration = await _host.RegisterPlayer("IngotStarve_Halt_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Seed the clean zero-ingot state: halt the refinery (ingot production → 0) and pin the ingot
        // buffer to 0, with an UnderConstruction building and an Active ship build both draining ingots.
        var (constructionIdx, shipId) = await SeedConsumers(store, planetId, at, starve: true);

        // Validate-on-arrival at `at`: production is 0 and the buffer is empty, so both consumers pause.
        await InvokeHandler(store, (session, bus) =>
            CheckIngotStarvedHandler.Handle(new CheckIngotStarved(planetId, at), session, bus));

        var after = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.ConstructionHalted, after.Buildings[constructionIdx].Status);
        Assert.Equal(at, after.Buildings[constructionIdx].HaltedAt);
        var shipBuild = after.ShipQueue.Single(b => b.Id == shipId);
        Assert.Equal(ShipBuildStatus.Halted, shipBuild.Status);
        Assert.Equal(at, shipBuild.HaltedAt);

        // The refinery stayed halted throughout — ingot production never returned, so the pauses hold.
        Assert.Equal(BuildingStatus.Halted, after.Buildings.Single(b => b.Type == BuildingType.Refinery).Status);
    }

    [Fact]
    public async Task DrillCompletionResumesRefineryThenIngotConsumersInOneCommit()
    {
        var registration = await _host.RegisterPlayer("IngotStarve_Resume_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Arrange the full scenario-2 tail: the refinery is halted InputStarved (ingot production → 0),
        // the ingot buffer is pinned to 0, and an UnderConstruction building + an Active ship build both
        // drain ingots. CheckIngotStarvedHandler then pauses both consumers (ConstructionHalted / Halted).
        var (constructionIdx, shipId) = await SeedConsumers(store, planetId, at, starve: true);
        await InvokeHandler(store, (session, bus) =>
            CheckIngotStarvedHandler.Handle(new CheckIngotStarved(planetId, at), session, bus));

        var halted = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.ConstructionHalted, halted.Buildings[constructionIdx].Status);
        Assert.Equal(ShipBuildStatus.Halted, halted.ShipQueue.Single(b => b.Id == shipId).Status);

        // Seed a fresh UnderConstruction Drill (lands right after the halted construction slot) that will
        // complete at at+50s. The homeworld drill already keeps CurrentOreInflow() > 0, so completing this
        // one gives CompleteBuildingConstructionHandler a BuildingCompleted to ride: it evaluates the
        // refinery resume (ore inflow present), applies it in-memory, then evaluates the ingot-consumer
        // resumes — all in ONE commit.
        var triggerIdx = constructionIdx + 1;
        var resumeAt = at.AddSeconds(50);
        await SeedTriggerDrill(store, planetId, triggerIdx, at, resumeAt);

        await InvokeHandler(store, (session, bus) =>
            CompleteBuildingConstructionHandler.Handle(
                new CompleteBuildingConstruction(planetId, triggerIdx, resumeAt), session, bus));

        // The commit un-starved the refinery AND resumed both consumers, each with CompletesAt pushed out
        // by exactly the paused span (resumeAt + remaining): construction was at+100s (remaining 100s) →
        // at+150s; the ship was at+30s (remaining 30s) → at+80s.
        var after = await FetchPlanet(store, planetId);
        Assert.Equal(
            BuildingStatus.Operational, after.Buildings.Single(b => b.Type == BuildingType.Refinery).Status);
        Assert.Equal(BuildingStatus.Operational, after.Buildings[triggerIdx].Status);

        var construction = after.Buildings[constructionIdx];
        Assert.Equal(BuildingStatus.UnderConstruction, construction.Status);
        Assert.Null(construction.HaltedAt);
        Assert.Equal(resumeAt.AddSeconds(100), construction.CompletesAt);

        var shipBuild = after.ShipQueue.Single(b => b.Id == shipId);
        Assert.Equal(ShipBuildStatus.Active, shipBuild.Status);
        Assert.Null(shipBuild.HaltedAt);
        Assert.Equal(resumeAt.AddSeconds(30), shipBuild.CompletesAt);
    }

    [Fact]
    public async Task StaleCheckWhileIngotsFlowingAppendsNothing()
    {
        var registration = await _host.RegisterPlayer("IngotStarve_Stale_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Same consumers, but the homeworld refinery stays Operational (ingot production > 0), so a
        // superseded/mis-predicted check is a no-op — nothing pauses.
        var (constructionIdx, shipId) = await SeedConsumers(store, planetId, at, starve: false);

        await InvokeHandler(store, (session, bus) =>
            CheckIngotStarvedHandler.Handle(new CheckIngotStarved(planetId, at), session, bus));

        var after = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.UnderConstruction, after.Buildings[constructionIdx].Status);
        Assert.Null(after.Buildings[constructionIdx].HaltedAt);
        Assert.Equal(ShipBuildStatus.Active, after.ShipQueue.Single(b => b.Id == shipId).Status);
    }

    // Seeds an UnderConstruction building (slot constructionIdx) and an Active ship build (shipId) onto the
    // homeworld stream, plus an Operational Shipyard so the ship build is bay-backed. When starve is true,
    // also halts the refinery (zeroes ingot production) and pins the IronIngot buffer to 0 (an oversized
    // CargoLoadedFromStorage clamps to 0 — the ingot analogue of StorageHaltingTests' oversized delivery).
    private static async Task<(int ConstructionIdx, Guid ShipId)> SeedConsumers(
        IDocumentStore store, Guid planetId, DateTimeOffset at, bool starve)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        Assert.NotNull(planet);

        var baseCount = planet.Buildings.Count;
        var shipyardIdx = baseCount;      // BuildingPlaced(Shipyard) lands here.
        var constructionIdx = baseCount + 1; // BuildingConstructionStarted lands right after it.
        var shipId = Guid.NewGuid();

        var events = new List<object>();
        if (starve)
        {
            var refineryIdx = IndexOfRefinery(planet);
            events.Add(new BuildingHalted(refineryIdx, HaltReason.InputStarved, at));
        }

        events.Add(new BuildingPlaced(BuildingType.Shipyard, at));
        events.Add(new BuildingConstructionStarted(
            constructionIdx, BuildingType.Drill, at, at.AddSeconds(100), DrainPerSecond: 1m));
        events.Add(new ShipConstructionQueued(shipId, ShipType.ColonyShip, at, DrainPerSecond: 1m, BuildDurationSeconds: 30m));
        events.Add(new ShipConstructionStarted(shipId, at, at.AddSeconds(30)));

        if (starve)
        {
            // Oversized load clamps the ingot buffer to 0 (Apply(CargoLoadedFromStorage) checkpoints
            // then clamps CheckpointValue − amount into [0, cap]). Appended LAST so no later RebaseRates
            // reintroduces stored ingots at `at`.
            events.Add(new CargoLoadedFromStorage(Guid.NewGuid(), 0m, planet.IronIngot.StorageCapacity, at));
        }

        stream.AppendMany([.. events]);
        await session.SaveChangesAsync();

        return (constructionIdx, shipId);
    }

    // Appends a fresh UnderConstruction Drill at `slotIndex` (Apply(BuildingConstructionStarted) always
    // lands at Buildings.Count, so slotIndex MUST equal the current count) completing at `completesAt`.
    // Completing it is the deterministic ore-return trigger for the resume cascade. (The symmetric
    // ingot-buffer restore via cargo delivery is a documented follow-up, mirroring the already-deferred
    // ore cargo-resume path — so this test drives the resume through a completing Drill.)
    private static async Task SeedTriggerDrill(
        IDocumentStore store, Guid planetId, int slotIndex, DateTimeOffset startedAt, DateTimeOffset completesAt)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        stream.AppendMany([
            new BuildingConstructionStarted(slotIndex, BuildingType.Drill, startedAt, completesAt, DrainPerSecond: 1m),
        ]);
        await session.SaveChangesAsync();
    }

    private static int IndexOfRefinery(Planet planet)
    {
        for (var i = 0; i < planet.Buildings.Count; i++)
        {
            if (planet.Buildings[i].Type == BuildingType.Refinery)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Homeworld is expected to seed a Refinery.");
    }

    private static async Task<Planet> FetchPlanet(IDocumentStore store, Guid planetId)
    {
        await using var session = store.LightweightSession();
        var planet = await session.Events.FetchLatest<Planet>(planetId);
        Assert.NotNull(planet);
        return planet;
    }

    // Invokes the check handler in a fresh scope/session, mirroring DepletionCascadeTests.InvokeHandler
    // (a real IMessageBus so the handler's self-reschedule runs through the outbox harmlessly).
    private async Task InvokeHandler(IDocumentStore store, Func<IDocumentSession, IMessageBus, Task> handle)
    {
        using var scope = _host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await using var session = store.LightweightSession();
        await handle(session, bus);
    }
}
