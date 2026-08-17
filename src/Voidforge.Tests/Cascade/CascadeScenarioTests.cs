using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Wolverine;
using Xunit;

namespace Voidforge.Tests.Cascade;

// The four engine.md "Cascading Events" scenarios (§"Cascading Events", L48-52) proven as cohesive
// integration tests. engine.md L52: the engine must resolve each dependency chain "within a single
// checkpoint" to maintain a consistent state. Energy is never an event — it is re-derived by RebaseRates
// inside every composition-changing Apply — so a scenario resolves "within a single checkpoint" exactly
// when ONE handler/endpoint commit (one SaveChangesAsync) turns the trigger AND all of its downstream
// consequences (halts/resumes AND the energy re-derivation) into one consistent post-commit state.
//
// Driven deterministically via the DepletionCascadeTests/IngotStarvationCascadeTests pattern: direct
// handler invocation through InvokeHandler, live-aggregate deadline math (PredictDepletionDeadline), and
// pool pinning via oversized CargoLoadedFromStorage events — no wall-clock waits, no fixed sleeps.
//
// Coverage note (#71): the ore→refinery head (DepletionCascadeTests), the ingot-consumer tail
// (IngotStarvationCascadeTests), and the energy/demolition slices (PlanetHaltingTests / PlanetEnergyTests
// / PlanetDemolitionTests) already exist SPLIT. These tests fill the surveyed integration gaps —
// full-chain-on-an-overloaded-planet (1), the completion-drives-overload path (3), the demolish-endpoint
// path (4) — and stitch the split ore→ingot chain into one unbroken flow (2).
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class CascadeScenarioTests
{
    private readonly IAlbaHost _host;

    public CascadeScenarioTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    // Scenario 1 (engine.md: "Ore pool depletes → Drill halts (5% energy) → energy balance changes →
    // other buildings' productivity may shift"), on an OVERLOADED planet. The freed Drill energy must
    // resolve the overload in the SAME depletion commit.
    [Fact]
    public async Task DepletionOnOverloadedPlanetHaltsAllDrillsAndRecoversProductivityInOneCheckpoint()
    {
        var registration = await _host.RegisterPlayer("Cascade_S1_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // The homeworld seeds Drill (20 MW) + Refinery (30 MW) + Generator (100 MW gen). Add 3 more
        // operational Drills so demand = 4 Drills (80) + Refinery (30) = 110 MW > 100 gen → m = 100/110.
        await SeedOperationalDrills(store, planetId, count: 3, at);

        var overloaded = await FetchPlanet(store, planetId);
        Assert.Equal(110m, overloaded.GetEnergyConsumptionMw());
        Assert.Equal(100m / 110m, overloaded.GetProductivityMultiplier());
        Assert.True(overloaded.IronOreDeposit.Rate < 0m, "the four Drills must be draining the finite deposit.");

        // Compute the deposit-empty instant from the aggregate's OWN drain math. Fire the check a hair past
        // it (as the real poll-lagged scheduler would, ADR 0001) so the ResourcePool clamp guarantees an
        // empty deposit regardless of decimal rounding on the fractional-m extraction rate — no wall-clock wait.
        var depletion = overloaded.PredictDepletionDeadline(overloaded.IronOreDeposit.CheckpointTime);
        Assert.NotNull(depletion);
        var depletedAt = depletion.At.AddSeconds(1);

        // ONE checkpoint: CheckPoolDepletedHandler does a single SaveChangesAsync. EvaluateDepletion halts
        // every operational Drill (ResourceDepleted) and each BuildingHalted.Apply re-derives energy via
        // RebaseRates in that same commit, so the freed Drill energy resolves the overload (engine.md L52)
        // with NO separate energy event.
        await InvokeHandler(store, (session, bus) =>
            CheckPoolDepletedHandler.Handle(new CheckPoolDepleted(planetId, depletedAt), session, bus));

        var after = await FetchPlanet(store, planetId);
        var drills = after.Buildings.Where(b => b.Type == BuildingType.Drill).ToList();
        Assert.Equal(4, drills.Count);
        Assert.All(drills, d =>
        {
            Assert.Equal(BuildingStatus.Halted, d.Status);
            Assert.Equal(HaltReason.ResourceDepleted, d.HaltReason);
        });
        Assert.Equal(0m, after.IronOreDeposit.GetCurrentValue(depletedAt)); // the drained deposit reads empty.

        // Energy freed → overload resolved IN the depletion commit: the 4 halted Drills now draw only their
        // 5% idle floor (4 × 1 MW) + the still-Operational Refinery (30) = 34 MW < 100 gen → m recovers to 1.
        Assert.Equal(BuildingStatus.Operational, after.Buildings.Single(b => b.Type == BuildingType.Refinery).Status);
        Assert.Equal(34m, after.GetEnergyConsumptionMw());
        Assert.Equal(1m, after.GetProductivityMultiplier());
    }

    // Scenario 2 (engine.md: "Ore storage empties → Refinery halts (5% energy) → ingot production stops →
    // Shipyard halts → construction halts") as ONE unbroken flow: deposit depleted (Drill halted) → ore
    // buffer empty → Refinery InputStarved → ingot production stops → ingot buffer empty → the in-flight
    // building AND ship build both pause. The tail's single-checkpoint claim is that ONE CheckIngotStarved
    // commit pauses EVERY ingot consumer together (the shared planet-level ingot scalar drives both).
    [Fact]
    public async Task OreDepletionStarvesRefineryThenHaltsBothIngotConsumersAlongTheChain()
    {
        var registration = await _host.RegisterPlayer("Cascade_S2_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Head of the chain: the homeworld Drill is halted ResourceDepleted (deposit depleted → ore inflow 0),
        // with the ingot consumers (an UnderConstruction building + an Active, bay-backed ship build) in
        // flight. The Refinery is left Operational — the chain halts it below.
        var (constructionIdx, shipId) = await SeedChainConsumers(store, planetId, at);

        // Link 1→2: pin the ore buffer empty, then the ore-starvation check halts the Refinery InputStarved.
        await PinPoolToZero(store, planetId, ResourceType.IronOre, at);
        await InvokeHandler(store, (session, bus) =>
            CheckInputStarvedHandler.Handle(new CheckInputStarved(planetId, at), session, bus));

        var afterRefinery = await FetchPlanet(store, planetId);
        var refinery = afterRefinery.Buildings.Single(b => b.Type == BuildingType.Refinery);
        Assert.Equal(BuildingStatus.Halted, refinery.Status);
        Assert.Equal(HaltReason.InputStarved, refinery.HaltReason);
        // Ingot production stopped: with no Refinery output the two consumers only DRAIN the ingot buffer.
        Assert.True(
            afterRefinery.IronIngot.Rate <= 0m,
            $"ingot production must stop once the Refinery is starved: rate={afterRefinery.IronIngot.Rate}.");

        // Tail (engine.md L52): pin the ingot buffer empty, then a SINGLE CheckIngotStarved commit pauses
        // BOTH ingot consumers together — the building (ConstructionHalted) and the ship build (Halted).
        await PinPoolToZero(store, planetId, ResourceType.IronIngot, at);
        await InvokeHandler(store, (session, bus) =>
            CheckIngotStarvedHandler.Handle(new CheckIngotStarved(planetId, at), session, bus));

        var after = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.ConstructionHalted, after.Buildings[constructionIdx].Status);
        Assert.Equal(at, after.Buildings[constructionIdx].HaltedAt);
        var shipBuild = after.ShipQueue.Single(b => b.Id == shipId);
        Assert.Equal(ShipBuildStatus.Halted, shipBuild.Status);
        Assert.Equal(at, shipBuild.HaltedAt);
    }

    // Scenario 3 (engine.md: "New building comes online → energy consumption increases → planet may become
    // overloaded → all building productivity drops"). The real post-#26 path: overload is tipped by a
    // building COMPLETING (CompleteBuildingConstructionHandler), not by an immediate placement.
    [Fact]
    public async Task BuildingCompletionTipsPlanetIntoOverloadInTheCompletionCommit()
    {
        var registration = await _host.RegisterPlayer("Cascade_S3_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Homeworld demand is 50 MW (Drill 20 + Refinery 30) vs 100 MW gen. Add 2 operational Drills →
        // demand 90 MW, still under generation (m = 1). Then queue a Drill construction: while
        // UnderConstruction it draws NOTHING (construction consumes no energy), so demand stays 90, m = 1.
        // Its ingot drain is seeded to 0 so the pre/post ingot rate reflects only the energy throttle.
        await SeedOperationalDrills(store, planetId, count: 2, at);
        var completesAt = at.AddSeconds(50);
        var constructionIdx = await SeedZeroDrainDrillConstruction(store, planetId, at, completesAt);

        var beforeCompletion = await FetchPlanet(store, planetId);
        Assert.Equal(90m, beforeCompletion.GetEnergyConsumptionMw());
        Assert.Equal(1m, beforeCompletion.GetProductivityMultiplier());
        Assert.Equal(10m, beforeCompletion.IronIngot.Rate); // full-throttle Refinery output: 2 × 5 ore/s.

        // ONE checkpoint: CompleteBuildingConstructionHandler does a single SaveChangesAsync. The completing
        // Drill flips Operational (demand → 110 MW), and BuildingCompleted.Apply re-derives energy via
        // RebaseRates in that same commit, so the overload (m = 100/110) and the throttled dependent rates
        // appear together (engine.md L52).
        await InvokeHandler(store, (session, bus) =>
            CompleteBuildingConstructionHandler.Handle(
                new CompleteBuildingConstruction(planetId, constructionIdx, completesAt), session, bus));

        var after = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.Operational, after.Buildings[constructionIdx].Status);
        Assert.Equal(110m, after.GetEnergyConsumptionMw());
        Assert.Equal(100m / 110m, after.GetProductivityMultiplier());
        // Dependent pool rate scaled DOWN by the same m in the completion commit: the Refinery's ingot
        // output drops from 10 to 2 × (5 × m) as its throughput is energy-throttled.
        Assert.True(after.IronIngot.Rate < beforeCompletion.IronIngot.Rate, "the Refinery output must scale down.");
        Assert.Equal(
            BuildingSpecs.RefineryIngotOutputFactor
                * (BuildingSpecs.RefineryOreConsumptionPerSecond(BuildingType.Refinery) * after.GetProductivityMultiplier()),
            after.IronIngot.Rate);
    }

    // Scenario 4 (engine.md: "Building demolished → energy consumption decreases → planet may exit overload
    // → productivity recovers"), through the real demolish ENDPOINT. Step 1 of the teardown
    // (BuildingDemolitionStarted) is the immediate shutdown, so the overload lifts in that one endpoint commit.
    [Fact]
    public async Task DemolishingAConsumerResolvesOverloadInTheDemolitionCommit()
    {
        var registration = await _host.RegisterPlayer("Cascade_S4_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var at = DateTimeOffset.UtcNow;

        // Overload: homeworld Drill (20) + Refinery (30) + 3 seeded Drills (60) = 110 MW > 100 gen → m = 100/110.
        await SeedOperationalDrills(store, planetId, count: 3, at);
        var overloaded = await FetchPlanet(store, planetId);
        Assert.Equal(110m, overloaded.GetEnergyConsumptionMw());
        Assert.Equal(100m / 110m, overloaded.GetProductivityMultiplier());

        // Demolish one seeded Drill (slot 3) via the endpoint. Step 1 (BuildingDemolitionStarted) leaves the
        // Operational set (a Demolishing building draws nothing), and its Apply re-derives energy via
        // RebaseRates in that SINGLE endpoint commit — freed energy resolves the overload (engine.md L52).
        const int demolishedSlot = 3;
        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{planetId}/buildings/{demolishedSlot}/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(202);
        });

        var after = await FetchPlanet(store, planetId);
        Assert.Equal(BuildingStatus.Demolishing, after.Buildings[demolishedSlot].Status);
        // Demand dropped to 3 Drills (60) + Refinery (30) = 90 MW < 100 gen → productivity fully recovered.
        Assert.Equal(90m, after.GetEnergyConsumptionMw());
        Assert.Equal(1m, after.GetProductivityMultiplier());
    }

    // Seeds `count` additional Operational Drills onto the homeworld stream (BuildingPlaced lands
    // Operational immediately, like registration's homeworld seeding). Each adds 20 MW of draw and 10 ore/s
    // of inflow — the lever used to push demand past the 100 MW generation so the planet is overloaded.
    private static async Task SeedOperationalDrills(IDocumentStore store, Guid planetId, int count, DateTimeOffset at)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var events = new List<object>();
        for (var i = 0; i < count; i++)
        {
            events.Add(new BuildingPlaced(BuildingType.Drill, at));
        }

        stream.AppendMany([.. events]);
        await session.SaveChangesAsync();
    }

    // Seeds one UnderConstruction Drill at the next slot (Apply lands at Buildings.Count) completing at
    // `completesAt`, with ZERO ingot drain so the pre/post-completion ingot rate isolates the energy
    // throttle from construction draw. Returns its slot index.
    private static async Task<int> SeedZeroDrainDrillConstruction(
        IDocumentStore store, Guid planetId, DateTimeOffset startedAt, DateTimeOffset completesAt)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        Assert.NotNull(planet);
        var slotIndex = planet.Buildings.Count;
        stream.AppendMany([
            new BuildingConstructionStarted(slotIndex, BuildingType.Drill, startedAt, completesAt, DrainPerSecond: 0m),
        ]);
        await session.SaveChangesAsync();
        return slotIndex;
    }

    // Seeds scenario 2's chain state onto the homeworld stream: the homeworld Drill halted ResourceDepleted
    // (the post-depletion head — ore inflow 0), an Operational Shipyard (so the ship build is bay-backed),
    // an UnderConstruction building and an Active ship build (the two ingot consumers). The Refinery is left
    // Operational so the chain halts it via CheckInputStarvedHandler. Mirrors IngotStarvationCascadeTests.
    private static async Task<(int ConstructionIdx, Guid ShipId)> SeedChainConsumers(
        IDocumentStore store, Guid planetId, DateTimeOffset at)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        Assert.NotNull(planet);

        var drillIdx = IndexOfBuilding(planet, BuildingType.Drill);
        var constructionIdx = planet.Buildings.Count + 1; // BuildingConstructionStarted lands right after the Shipyard.
        var shipId = Guid.NewGuid();

        stream.AppendMany([
            new BuildingHalted(drillIdx, HaltReason.ResourceDepleted, at),
            new BuildingPlaced(BuildingType.Shipyard, at),
            new BuildingConstructionStarted(constructionIdx, BuildingType.Drill, at, at.AddSeconds(100), DrainPerSecond: 1m),
            new ShipConstructionQueued(shipId, ShipType.ColonyShip, at, DrainPerSecond: 1m, BuildDurationSeconds: 30m),
            new ShipConstructionStarted(shipId, at, at.AddSeconds(30)),
        ]);
        await session.SaveChangesAsync();
        return (constructionIdx, shipId);
    }

    // Pins a stored pool to 0 at `at` via an oversized CargoLoadedFromStorage — Apply clamps
    // (CheckpointValue − amount) into [0, cap], so loading the whole capacity zeroes the pool while leaving
    // the other pool untouched. The documented deterministic buffer-empty technique (mirrors
    // StorageHaltingTests / IngotStarvationCascadeTests) — no wall-clock drain wait.
    private static async Task PinPoolToZero(
        IDocumentStore store, Guid planetId, ResourceType resource, DateTimeOffset at)
    {
        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        Assert.NotNull(planet);
        var ore = resource == ResourceType.IronOre ? planet.IronOre.StorageCapacity : 0m;
        var ingot = resource == ResourceType.IronIngot ? planet.IronIngot.StorageCapacity : 0m;
        stream.AppendMany([new CargoLoadedFromStorage(Guid.NewGuid(), ore, ingot, at)]);
        await session.SaveChangesAsync();
    }

    private static int IndexOfBuilding(Planet planet, BuildingType type)
    {
        for (var i = 0; i < planet.Buildings.Count; i++)
        {
            if (planet.Buildings[i].Type == type)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Homeworld is expected to seed a {type}.");
    }

    private static async Task<Planet> FetchPlanet(IDocumentStore store, Guid planetId)
    {
        await using var session = store.LightweightSession();
        var planet = await session.Events.FetchLatest<Planet>(planetId);
        Assert.NotNull(planet);
        return planet;
    }

    // Invokes a check handler in a fresh scope/session, mirroring DepletionCascadeTests.InvokeHandler
    // (a real IMessageBus so the handler's self-reschedule runs through the outbox harmlessly).
    private async Task InvokeHandler(IDocumentStore store, Func<IDocumentSession, IMessageBus, Task> handle)
    {
        using var scope = _host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await using var session = store.LightweightSession();
        await handle(session, bus);
    }
}
