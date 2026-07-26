using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
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
        var registration = await RegisterPlayer();

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
        var owner = await RegisterPlayer();
        var intruder = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, Guid.NewGuid()))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task LaunchUnsupportedMissionReturns400()
    {
        var registration = await RegisterPlayer();

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Transport, Guid.NewGuid()))
                .ToUrl($"/api/fleets/{Guid.NewGuid()}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task LaunchToCurrentLocationReturns400()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, owner.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task LaunchToUnknownDestinationReturns404()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

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
        var owner = await RegisterPlayer();
        var beta = await RegisterPlayer();   // another colonized planet to travel to
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        var launched = await Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Null(launched.LocationPlanetId);
        Assert.Equal(owner.HomeworldId, launched.OriginPlanetId);
        Assert.Equal(beta.HomeworldId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Move, launched.Mission);
        Assert.NotNull(launched.DepartedAt);
        Assert.NotNull(launched.ArrivesAt);

        // Round-trip through Postgres (not just the launch response): a fresh GET must
        // deserialize the same mid-transit snapshot, including its nested TravelPlan.
        var fetched = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
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
        var owner = await RegisterPlayer();
        var beta = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);
        await Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

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
        var owner = await RegisterPlayer();
        var beta = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);
        var launched = await Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        // Never dispose the DI-owned IDocumentStore (technical-design/testing.md) — only the
        // session it hands out.
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), session);
        }

        var arrived = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
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

        var afterDuplicate = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(beta.HomeworldId, afterDuplicate.LocationPlanetId);

        // A message with a stale/wrong ArrivesAt: also a no-op.
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(
                new CompleteFleetArrival(fleet.Id, arrivesAt.AddSeconds(1)), session);
        }

        var afterStale = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterStale.Status);
        Assert.Equal(beta.HomeworldId, afterStale.LocationPlanetId);
    }

    private async Task<FleetResponse> Launch(
        RegisterPlayerResponse registration, Guid fleetId, MissionType mission, Guid destinationPlanetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(mission, destinationPlanetId)).ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(response);
        return response;
    }

    // Builds an operational shipyard, queues one CargoVessel (~2s build), and polls the
    // roster until it appears. Returns the completed ship's id.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration)
    {
        await BuildOperationalShipyard(registration);
        await QueueShip(registration, ShipType.CargoVessel);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            var roster = await GetRoster(registration);
            if (roster.Items.Count > 0)
            {
                return roster.Items[0].Id;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
    }

    private async Task<FleetResponse> AssembleFleet(RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds)).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    private async Task<T> GetJson<T>(RegisterPlayerResponse registration, string url)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<T>();
        Assert.NotNull(response);
        return response;
    }

    private async Task BuildOperationalShipyard(RegisterPlayerResponse registration)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        await PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == BuildingType.Shipyard && b.Status == BuildingStatus.Operational),
            TimeSpan.FromSeconds(20));
    }

    private async Task<ShipBuildResponse> QueueShip(RegisterPlayerResponse registration, ShipType type)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new QueueShipRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var build = await result.ReadAsJsonAsync<ShipBuildResponse>();
        Assert.NotNull(build);
        return build;
    }

    private async Task<PagedResponse<RosterShipResponse>> GetRoster(RegisterPlayerResponse registration)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}/ships?pageSize=200");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var roster = await result.ReadAsJsonAsync<PagedResponse<RosterShipResponse>>();
        Assert.NotNull(roster);
        return roster;
    }

    private async Task<PlanetResponse> PollUntil(
        RegisterPlayerResponse registration, Func<PlanetResponse, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        PlanetResponse planet;
        do
        {
            planet = await GetPlanet(registration);
            if (predicate(planet))
            {
                return planet;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return planet;
    }

    private async Task<PlanetResponse> GetPlanet(RegisterPlayerResponse registration)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await result.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);
        return planet;
    }

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"FleetMission_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
