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

// Depletion cascade e2e (#70 acceptance): the finite ore deposit empties → every Drill halts
// PERMANENTLY (ResourceDepleted) → the still-Operational Refinery draws the stored ore buffer down →
// once the buffer runs dry it halts InputStarved and ingot production stops. Driven deterministically
// by invoking the two scheduled-check handlers directly at instants computed from the live aggregate's
// OWN drain math (mirrors StorageHaltingTests) — no wall-clock waits, no fixed sleeps.
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class DepletionCascadeTests
{
    private readonly IAlbaHost _host;

    public DepletionCascadeTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task DepletionHaltsDrillThenStarvesRefineryWhenBufferEmpties()
    {
        var registration = await _host.RegisterPlayer("DepletionCascade_");
        var planetId = registration.HomeworldId;
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // The homeworld seeds an Operational Drill draining a finite deposit — read the live deposit and
        // compute the exact empty instant from its own checkpoint/rate so depletion fires without a wait.
        var seeded = await FetchPlanet(store, planetId);
        Assert.True(seeded.IronOreDeposit.Rate < 0m, "Precondition: the deposit must be draining under the Drill.");
        var depletion = seeded.PredictDepletionDeadline(seeded.IronOreDeposit.CheckpointTime);
        Assert.NotNull(depletion);
        var depletedAt = depletion.At;

        // --- Cascade step 1: the deposit empties → the Drill halts ResourceDepleted, deposit reads 0. ---
        await InvokeHandler(store, (session, bus) =>
            CheckPoolDepletedHandler.Handle(new CheckPoolDepleted(planetId, depletedAt), session, bus));

        var afterDepletion = await _host.GetPlanet(registration);
        var drill = afterDepletion.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Halted, drill.Status);
        Assert.Equal(HaltReason.ResourceDepleted, drill.HaltReason);
        Assert.Equal(0m, afterDepletion.IronOrePool); // the drained deposit reads empty.

        // With the Drill halted, the still-Operational Refinery draws the buffer at a negative rate and
        // keeps producing ingots (rate > 0) until the buffer runs dry.
        var refineryWhileDraining = afterDepletion.Buildings.Single(b => b.Type == BuildingType.Refinery);
        Assert.Equal(BuildingStatus.Operational, refineryWhileDraining.Status);
        Assert.True(afterDepletion.IronOre.Rate < 0m, "the Refinery must drain the buffer once drill inflow stops.");
        Assert.True(afterDepletion.IronIngot.Rate > 0m, "ingots still flow while the buffer feeds the Refinery.");

        // Compute the buffer-empty instant from the post-depletion aggregate's own drain math.
        var draining = await FetchPlanet(store, planetId);
        var bufferEmpty = draining.PredictBufferEmpty(depletedAt);
        Assert.NotNull(bufferEmpty);
        var bufferEmptyAt = bufferEmpty.At;

        // --- Cascade step 2: the buffer empties → the Refinery halts InputStarved, ingot rate → 0. ---
        await InvokeHandler(store, (session, bus) =>
            CheckInputStarvedHandler.Handle(new CheckInputStarved(planetId, bufferEmptyAt), session, bus));

        var afterStarvation = await _host.GetPlanet(registration);
        var refinery = afterStarvation.Buildings.Single(b => b.Type == BuildingType.Refinery);
        Assert.Equal(BuildingStatus.Halted, refinery.Status);
        Assert.Equal(HaltReason.InputStarved, refinery.HaltReason);
        Assert.True(
            afterStarvation.IronIngot.Rate <= 0m,
            $"ingot production must stop once the Refinery is starved: rate={afterStarvation.IronIngot.Rate}.");

        // The depleted Drill stays permanently halted throughout — depletion never resumes (#70).
        var drillFinal = afterStarvation.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Halted, drillFinal.Status);
        Assert.Equal(HaltReason.ResourceDepleted, drillFinal.HaltReason);
    }

    private static async Task<Planet> FetchPlanet(IDocumentStore store, Guid planetId)
    {
        await using var session = store.LightweightSession();
        var planet = await session.Events.FetchLatest<Planet>(planetId);
        Assert.NotNull(planet);
        return planet;
    }

    // Invokes a check handler in a fresh scope/session, mirroring StorageHaltingTests' direct-invocation
    // style (a real IMessageBus so the handler's self-reschedule runs through the outbox harmlessly).
    private async Task InvokeHandler(IDocumentStore store, Func<IDocumentSession, IMessageBus, Task> handle)
    {
        using var scope = _host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await using var session = store.LightweightSession();
        await handle(session, bus);
    }
}
