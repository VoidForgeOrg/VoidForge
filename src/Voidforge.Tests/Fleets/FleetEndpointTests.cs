using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Tests.Support;
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
        var registration = await _host.RegisterPlayer("Fleet_Test_");
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
        var registration = await _host.RegisterPlayer("Fleet_Test_");
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
        var registration = await _host.RegisterPlayer("Fleet_Test_");
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
        var registration = await _host.RegisterPlayer("Fleet_Test_");
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
        var owner = await _host.RegisterPlayer("Fleet_Test_");          // builds the ships
        var intruder = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(owner);   // shipyard + 1 CargoVessel, waits for roster

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
        var registration = await _host.RegisterPlayer("Fleet_Test_");
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
        var owner = await _host.RegisterPlayer("Fleet_Test_");
        var intruder = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

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
        var owner = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(owner);

        var fleet = await _host.AssembleFleet(owner, [shipId]);

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(owner.HomeworldId, fleet.LocationPlanetId);
        Assert.Equal(owner.PlayerId, fleet.OwnerId);
        var fleetShip = Assert.Single(fleet.Ships);
        Assert.Equal(shipId, fleetShip.Id);

        // The ship left the planet's roster.
        var roster = await _host.GetRoster(owner);
        Assert.DoesNotContain(roster.Items, r => r.Id == shipId);
    }

    [Fact]
    public async Task DisbandReturnsShipsToTheRoster()
    {
        var owner = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

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

        var roster = await _host.GetRoster(owner);
        Assert.Contains(roster.Items, r => r.Id == shipId);
    }

    [Fact]
    public async Task DisbandTwiceReturns409()
    {
        var owner = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

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
        var a = await _host.RegisterPlayer("Fleet_Test_");
        var b = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(a);
        var fleet = await _host.AssembleFleet(a, [shipId]);

        var page = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(b, "/api/fleets");
        Assert.DoesNotContain(page.Items, f => f.Id == fleet.Id);   // b sees only own fleets

        var own = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
        var summary = Assert.Single(own.Items, f => f.Id == fleet.Id);
        Assert.Equal(1, summary.ShipCount);
    }

    [Fact]
    public async Task FleetDetailIsUniverseVisible()
    {
        var a = await _host.RegisterPlayer("Fleet_Test_");
        var b = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(a);
        var fleet = await _host.AssembleFleet(a, [shipId]);

        var detail = await _host.GetJson<FleetResponse>(b, $"/api/fleets/{fleet.Id}");
        Assert.Equal(fleet.Id, detail.Id);
        Assert.Single(detail.Ships);
    }

    [Fact]
    public async Task PlanetFleetsListsStationedFleets()
    {
        var a = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(a);
        var fleet = await _host.AssembleFleet(a, [shipId]);

        var page = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(a, $"/api/planets/{a.HomeworldId}/fleets");
        Assert.Contains(page.Items, f => f.Id == fleet.Id);
    }

    [Fact]
    public async Task DisbandedFleetsAreExcludedFromListsUnlessRequested()
    {
        var a = await _host.RegisterPlayer("Fleet_Test_");
        var shipId = await _host.BuildRosterShip(a);
        var fleet = await _host.AssembleFleet(a, [shipId]);
        await _host.Disband(a, fleet.Id);

        var live = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
        Assert.DoesNotContain(live.Items, f => f.Id == fleet.Id);

        var history = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets?status=Disbanded");
        Assert.Contains(history.Items, f => f.Id == fleet.Id);
    }
}
