using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Fleets;

[Collection(IntegrationCollection.Name)]
public sealed class FleetEndpointTests
{
    private readonly IAlbaHost _host;

    public FleetEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task AssembleWithEmptyShipIdsReturns400()
    {
        var registration = await RegisterPlayer();
        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([])).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task AssembleWithNullShipIdsReturns400()
    {
        var registration = await RegisterPlayer();
        await _host.Scenario(s =>
        {
            s.Post.Json(new { }).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task AssembleUnknownPlanetReturns404()
    {
        var registration = await RegisterPlayer();
        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([Guid.NewGuid()])).ToUrl($"/api/planets/{Guid.NewGuid()}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task AssembleShipNotOnRosterReturns409()
    {
        var registration = await RegisterPlayer();
        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([Guid.NewGuid()])).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task AssembleSomeoneElsesShipsReturns403()
    {
        var owner = await RegisterPlayer();          // builds the ships
        var intruder = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);   // shipyard + 1 CargoVessel, waits for roster

        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([shipId])).ToUrl($"/api/planets/{owner.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task DisbandUnknownFleetReturns404()
    {
        var registration = await RegisterPlayer();
        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{Guid.NewGuid()}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task DisbandForeignFleetReturns403()
    {
        var owner = await RegisterPlayer();
        var intruder = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task AssembleRosterShipsProducesAStationedFleet()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);

        var fleet = await AssembleFleet(owner, [shipId]);

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(owner.HomeworldId, fleet.LocationPlanetId);
        Assert.Equal(owner.PlayerId, fleet.OwnerId);
        var fleetShip = Assert.Single(fleet.Ships);
        Assert.Equal(shipId, fleetShip.Id);

        // The ship left the planet's roster.
        var roster = await GetRoster(owner);
        Assert.DoesNotContain(roster.Items, r => r.Id == shipId);
    }

    [Fact]
    public async Task DisbandReturnsShipsToTheRoster()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var disbanded = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(disbanded);
        Assert.Equal(FleetStatus.Disbanded, disbanded.Status);
        Assert.Empty(disbanded.Ships);

        var roster = await GetRoster(owner);
        Assert.Contains(roster.Items, r => r.Id == shipId);
    }

    [Fact]
    public async Task DisbandTwiceReturns409()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task OwnFleetsListIsPaginatedAndScopedToCaller()
    {
        var a = await RegisterPlayer();
        var b = await RegisterPlayer();
        var shipId = await BuildRosterShip(a);
        var fleet = await AssembleFleet(a, [shipId]);

        var page = await GetJson<PagedResponse<FleetSummaryResponse>>(b, "/api/fleets");
        Assert.DoesNotContain(page.Items, f => f.Id == fleet.Id);   // b sees only own fleets

        var own = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
        var summary = Assert.Single(own.Items, f => f.Id == fleet.Id);
        Assert.Equal(1, summary.ShipCount);
    }

    [Fact]
    public async Task FleetDetailIsUniverseVisible()
    {
        var a = await RegisterPlayer();
        var b = await RegisterPlayer();
        var shipId = await BuildRosterShip(a);
        var fleet = await AssembleFleet(a, [shipId]);

        var detail = await GetJson<FleetResponse>(b, $"/api/fleets/{fleet.Id}");
        Assert.Equal(fleet.Id, detail.Id);
        Assert.Single(detail.Ships);
    }

    [Fact]
    public async Task PlanetFleetsListsStationedFleets()
    {
        var a = await RegisterPlayer();
        var shipId = await BuildRosterShip(a);
        var fleet = await AssembleFleet(a, [shipId]);

        var page = await GetJson<PagedResponse<FleetSummaryResponse>>(a, $"/api/planets/{a.HomeworldId}/fleets");
        Assert.Contains(page.Items, f => f.Id == fleet.Id);
    }

    [Fact]
    public async Task DisbandedFleetsAreExcludedFromListsUnlessRequested()
    {
        var a = await RegisterPlayer();
        var shipId = await BuildRosterShip(a);
        var fleet = await AssembleFleet(a, [shipId]);
        await Disband(a, fleet.Id);

        var live = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
        Assert.DoesNotContain(live.Items, f => f.Id == fleet.Id);

        var history = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets?status=Disbanded");
        Assert.Contains(history.Items, f => f.Id == fleet.Id);
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

    private async Task<FleetResponse> Disband(RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
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
            s.Post.Json(new RegisterPlayerRequest($"Fleet_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
