using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Travel;

[Collection(IntegrationCollection.Name)]
public sealed class FleetMissionEndpointTests
{
    private readonly IAlbaHost _host;

    public FleetMissionEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchUnknownFleetReturns404()
    {
        var registration = await _host.RegisterPlayer("FleetMission_Test_");

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, Guid.NewGuid()))
                .ToUrl($"/api/fleets/{Guid.NewGuid()}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task LaunchForeignFleetReturns403()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var intruder = await _host.RegisterPlayer("FleetMission_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, Guid.NewGuid()))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task LaunchUnknownMissionTypeReturns400()
    {
        var registration = await _host.RegisterPlayer("FleetMission_Test_");

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { mission = 99, destinationPlanetId = Guid.NewGuid() })
                .ToUrl($"/api/fleets/{Guid.NewGuid()}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(400);
        });

        var body = result.ReadAsText();
        Assert.Contains("Unknown mission type.", body, StringComparison.Ordinal);
    }

    // #60: the same-destination 400 stays in force for Move/Transport (no journey to make).
    // Colonize-in-place is the deliberate exception — covered by
    // LaunchColonizeToCurrentLocationIsAllowedAndClaimsTheParkedPlanet below.
    [Fact]
    public async Task LaunchToCurrentLocationReturns400()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, owner.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    // #60: Colonize to the fleet's current location is allowed (unlike Move/Transport). A colony
    // fleet parked at an uncolonized world can claim it in place — a zero-distance plan arrives
    // immediately and the guarded claim on arrival decides the outcome.
    [Fact]
    public async Task LaunchColonizeToCurrentLocationIsAllowedAndClaimsTheParkedPlanet()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId]);
        var target = await _host.FindUncolonizedPlanet(owner);

        // Park the fleet at the uncolonized world (Move consumes nothing — the colony ship survives).
        var parked = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Move, target);
        Assert.Equal(FleetStatus.Stationed, parked.Status);
        Assert.Equal(target, parked.LocationPlanetId);
        Assert.Contains(parked.Ships, s => s.Id == colonyShipId);

        // Colonize-in-place: destination == the fleet's current location (previously a 400).
        var claimed = await _host.LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Colonize, target);
        Assert.Equal(FleetStatus.Stationed, claimed.Status);
        Assert.Equal(target, claimed.LocationPlanetId);
        Assert.DoesNotContain(claimed.Ships, s => s.Id == colonyShipId);   // consumed on the claim

        var planet = await _host.GetPlanetById(owner, target);
        Assert.Equal(owner.PlayerId, planet.OwnerId);
    }

    [Fact]
    public async Task LaunchToUnknownDestinationReturns404()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, Guid.NewGuid()))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task LaunchMoveTransitionsFleetToInTransitAndRoundTripsThroughTheApi()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var beta = await _host.RegisterPlayer("FleetMission_Test_");   // another colonized planet to travel to
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Null(launched.LocationPlanetId);
        Assert.Equal(owner.HomeworldId, launched.OriginPlanetId);
        Assert.Equal(beta.HomeworldId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Move, launched.Mission);
        Assert.NotNull(launched.DepartedAt);
        Assert.NotNull(launched.ArrivesAt);

        // Round-trip through Postgres (not just the launch response): a fresh GET must
        // deserialize the same mid-transit snapshot, including its nested TravelPlan.
        var fetched = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.InTransit, fetched.Status);
        Assert.Null(fetched.LocationPlanetId);
        Assert.Equal(owner.HomeworldId, fetched.OriginPlanetId);
        Assert.Equal(beta.HomeworldId, fetched.DestinationPlanetId);
        Assert.Equal(MissionType.Move, fetched.Mission);
        Assert.Equal(launched.DepartedAt, fetched.DepartedAt);
        Assert.Equal(launched.ArrivesAt, fetched.ArrivesAt);
    }

    [Fact]
    public async Task LaunchWhileInTransitReturns409()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var beta = await _host.RegisterPlayer("FleetMission_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);
        await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, beta.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task HandlerInvokedArrivalStationsTheFleetAndIsIdempotent()
    {
        var owner = await _host.RegisterPlayer("FleetMission_Test_");
        var beta = await _host.RegisterPlayer("FleetMission_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);
        var launched = await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        // Never dispose the DI-owned IDocumentStore (technical-design/testing.md) — only the
        // session it hands out.
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), session);
        }

        var arrived = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(beta.HomeworldId, arrived.LocationPlanetId);
        Assert.Null(arrived.OriginPlanetId);
        Assert.Null(arrived.DestinationPlanetId);
        Assert.Null(arrived.Mission);
        Assert.Null(arrived.DepartedAt);
        Assert.Null(arrived.ArrivesAt);

        // Duplicate delivery of the exact same message: no-op (the fleet is no longer InTransit).
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), session);
        }

        var afterDuplicate = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(beta.HomeworldId, afterDuplicate.LocationPlanetId);

        // A message with a stale/wrong ArrivesAt: also a no-op.
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(
                new CompleteFleetArrival(fleet.Id, arrivesAt.AddSeconds(1)), session);
        }

        var afterStale = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterStale.Status);
        Assert.Equal(beta.HomeworldId, afterStale.LocationPlanetId);
    }
}
