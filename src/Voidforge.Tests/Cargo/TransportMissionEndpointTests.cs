using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Cargo;

// #50 (spec §2.4): Transport mission launch guards + the codebase's first
// cross-aggregate arrival append (Planet storage credited, Fleet cargo zeroed, one commit).
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class TransportMissionEndpointTests
{
    private readonly IAlbaHost _host;

    public TransportMissionEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchTransportToForeignDestinationReturns403()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var foreign = await _host.RegisterPlayer("Transport_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Transport, foreign.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task LaunchTransportToOwnDestinationReturns200AndTransitionsToInTransit()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);
        // #51 makes a second owned planet reachable via the API (colonization) — arranged
        // directly here since Transport's same-owner destination requirement needs one now.
        var destinationId = await ColonizeSecondPlanetForOwner(owner);

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(destinationId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Transport, launched.Mission);
    }

    [Fact]
    public async Task HandlerInvokedTransportArrivalCreditsDestinationAndZeroesFleetCargo()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // empty storage: full headroom

        var arrived = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.Equal(0m, arrived.CargoIronOre);
        Assert.Equal(0m, arrived.CargoIronIngot);

        var destination = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(100m, destination.IronOre.CurrentValue);
        Assert.Equal(50m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task DuplicateTransportArrivalIsANoOpAndDoesNotDoubleDeliverCargo()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // empty storage: full headroom

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Transport, destinationId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await _host.CompleteArrivalWithRetry(fleet.Id, arrivesAt);

        var afterFirst = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(0m, afterFirst.CargoIronOre);
        Assert.Equal(0m, afterFirst.CargoIronIngot);
        var destinationAfterFirst = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(100m, destinationAfterFirst.IronOre.CurrentValue);
        Assert.Equal(50m, destinationAfterFirst.IronIngot.CurrentValue);

        // Redelivery of the identical message (Wolverine's at-least-once delivery, ADR 0001):
        // the fleet is no longer InTransit, so Arrive() returns no events and the handler
        // returns before ever touching the Planet stream — must not double-credit storage.
        await using (var secondSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), secondSession);
        }

        var afterDuplicate = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(destinationId, afterDuplicate.LocationPlanetId);
        Assert.Equal(0m, afterDuplicate.CargoIronOre);
        Assert.Equal(0m, afterDuplicate.CargoIronIngot);

        var destinationAfterDuplicate = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(destinationAfterFirst.IronOre.CurrentValue, destinationAfterDuplicate.IronOre.CurrentValue);
        Assert.Equal(destinationAfterFirst.IronIngot.CurrentValue, destinationAfterDuplicate.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedTransportArrivalWithFullDestinationStorageLeavesCargoAboard()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        // Colonize the destination already at its storage cap (WorldGenOptions defaults:
        // 10000 ore / 5000 ingot) so AcceptCargoDelivery has zero headroom for either pool.
        var destinationId = await ColonizeSecondPlanetForOwner(owner, ironOreStored: 10_000, ironIngotStored: 5_000);

        var arrived = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        // Nothing fit: cargo stays aboard rather than being lost.
        Assert.Equal(100m, arrived.CargoIronOre);
        Assert.Equal(50m, arrived.CargoIronIngot);

        var destination = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(10_000m, destination.IronOre.CurrentValue);
        Assert.Equal(5_000m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedMoveArrivalWithCargoLeavesCargoUntouched()
    {
        var owner = await _host.RegisterPlayer("Transport_Test_");
        var beta = await _host.RegisterPlayer("Transport_Test_");   // another colonized planet to Move to
        var shipId = await _host.BuildRosterShip(owner);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(80m, 20m));

        var arrived = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(beta.HomeworldId, arrived.LocationPlanetId);
        // Move never auto-delivers cargo (spec §2.4) — only Transport/Colonize do.
        Assert.Equal(80m, arrived.CargoIronOre);
        Assert.Equal(20m, arrived.CargoIronIngot);
    }

    // Test arrangement, not production code: #51 (colonization) is the API path that will
    // make a second same-owner planet reachable; until then, appending PlanetColonized
    // directly to an uncolonized planet's stream is how Transport's happy paths get a
    // same-owner destination to target. Mirrors PlayerEndpoints.Register's colonization.
    private async Task<Guid> ColonizeSecondPlanetForOwner(
        RegisterPlayerResponse owner, long ironOreStored = 0, long ironIngotStored = 0)
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();
        var planetId = uncolonized[0];

        // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
        session.Events.Append(planetId, new PlanetColonized(owner.PlayerId, ironOreStored, ironIngotStored, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        return planetId;
    }
}
