using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Colonize;

// #51 (spec §2.4): Colonize mission launch guard (Colony Ship required) + the
// guarded arrival claim (planet owned by the fleet owner, colony ship consumed, cargo
// delivered) vs. the lost-the-race/already-owned branch (fleet idles Stationed, nothing
// aboard changes).
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class ColonizeMissionTests
{
    private readonly IAlbaHost _host;

    public ColonizeMissionTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchColonizeWithoutAColonyShipAboardReturns409()
    {
        var owner = await _host.RegisterPlayer("Colonize_Test_");
        var foreign = await _host.RegisterPlayer("Colonize_Test_");
        var shipId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Colonize, foreign.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task LaunchColonizeWithAColonyShipAboardReturns200AndTransitionsToInTransit()
    {
        var owner = await _host.RegisterPlayer("Colonize_Test_");
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId]);
        var destinationId = await UncolonizedPlanetId();

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(destinationId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Colonize, launched.Mission);
    }

    [Fact]
    public async Task HandlerInvokedColonizeArrivalAtAnUncolonizedPlanetClaimsItConsumesTheColonyShipAndDeliversCargo()
    {
        var owner = await _host.RegisterPlayer("Colonize_Test_");
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await UncolonizedPlanetId();

        var arrived = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.DoesNotContain(arrived.Ships, s => s.Id == colonyShipId);   // consumed on claim
        Assert.Contains(arrived.Ships, s => s.Id == cargoVesselId);        // untouched
        Assert.Equal(0m, arrived.CargoIronOre);
        Assert.Equal(0m, arrived.CargoIronIngot);

        var destination = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destination.OwnerId);
        Assert.Equal(100m, destination.IronOre.CurrentValue);
        Assert.Equal(50m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedColonizeArrivalAtAnAlreadyOwnedPlanetLeavesOwnerShipAndCargoIntactAndFleetStationed()
    {
        var owner = await _host.RegisterPlayer("Colonize_Test_");
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // already owned before arrival

        var arrived = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.Contains(arrived.Ships, s => s.Id == colonyShipId);    // ship preserved: lost the race
        Assert.Contains(arrived.Ships, s => s.Id == cargoVesselId);
        Assert.Equal(100m, arrived.CargoIronOre);                     // cargo intact: never delivered
        Assert.Equal(50m, arrived.CargoIronIngot);

        var destination = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destination.OwnerId);   // owner unchanged
        Assert.Equal(0m, destination.IronOre.CurrentValue);
        Assert.Equal(0m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task DuplicateColonizeArrivalIsANoOpAndDoesNotDoubleClaimOrDoubleDeliverCargo()
    {
        var owner = await _host.RegisterPlayer("Colonize_Test_");
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await UncolonizedPlanetId();

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Colonize, destinationId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await _host.CompleteArrivalWithRetry(fleet.Id, arrivesAt);

        var afterFirst = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.DoesNotContain(afterFirst.Ships, s => s.Id == colonyShipId);
        Assert.Equal(0m, afterFirst.CargoIronOre);
        Assert.Equal(0m, afterFirst.CargoIronIngot);
        var destinationAfterFirst = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destinationAfterFirst.OwnerId);
        Assert.Equal(100m, destinationAfterFirst.IronOre.CurrentValue);
        Assert.Equal(50m, destinationAfterFirst.IronIngot.CurrentValue);

        // Redelivery of the identical message (Wolverine's at-least-once delivery, ADR 0001):
        // the fleet is no longer InTransit, so Arrive() returns no events and the handler
        // returns before ever touching the Planet stream — must not double-claim or
        // double-deliver cargo.
        await using (var secondSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), secondSession);
        }

        var afterDuplicate = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(destinationId, afterDuplicate.LocationPlanetId);
        Assert.Equal(afterFirst.Ships.Select(s => s.Id).OrderBy(id => id), afterDuplicate.Ships.Select(s => s.Id).OrderBy(id => id));
        Assert.Equal(0m, afterDuplicate.CargoIronOre);
        Assert.Equal(0m, afterDuplicate.CargoIronIngot);

        var destinationAfterDuplicate = await _host.GetPlanetById(owner, destinationId);
        Assert.Equal(destinationAfterFirst.OwnerId, destinationAfterDuplicate.OwnerId);
        Assert.Equal(destinationAfterFirst.IronOre.CurrentValue, destinationAfterDuplicate.IronOre.CurrentValue);
        Assert.Equal(destinationAfterFirst.IronIngot.CurrentValue, destinationAfterDuplicate.IronIngot.CurrentValue);
    }

    // Raw query for a planet nobody owns yet — the Colonize mission's natural destination.
    // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
    private async Task<Guid> UncolonizedPlanetId()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();

        return uncolonized[0];
    }

    // Test arrangement, not production code: appends PlanetColonized directly to an
    // uncolonized planet's stream so a Colonize arrival can be arranged against an
    // ALREADY-owned destination without depending on a second player's registration.
    // Mirrors TransportMissionEndpointTests.ColonizeSecondPlanetForOwner.
    // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
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

        session.Events.Append(planetId, new PlanetColonized(owner.PlayerId, ironOreStored, ironIngotStored, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        return planetId;
    }
}
