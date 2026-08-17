using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Ships;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class ShipEndpointTests
{
    private readonly IAlbaHost _host;

    public ShipEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task QueueShipIsAcceptedEvenWithNoShipyard()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");   // homeworld has no shipyard

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new QueueShipRequest(ShipType.ColonyShip))
                .ToUrl($"/api/planets/{registration.HomeworldId}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var build = await result.ReadAsJsonAsync<ShipBuildResponse>();
        Assert.NotNull(build);
        Assert.Equal(ShipType.ColonyShip, build.Type);
        Assert.Equal(ShipBuildStatus.Queued, build.Status);   // no shipyard => waits
    }

    [Fact]
    public async Task PlanetResponseReportsBoundedShipCounts()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");
        await _host.QueueShip(registration, ShipType.CargoVessel);

        var planet = await _host.GetPlanet(registration);
        Assert.Equal(0, planet.ShipCount);
        Assert.Equal(1, planet.QueueLength);
        Assert.Equal(0, planet.ActiveBuilds);   // no shipyard yet
    }

    [Fact]
    public async Task ShipQueueEndpointIsPaginated()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");
        for (var i = 0; i < 3; i++)
        {
            await _host.QueueShip(registration, ShipType.ColonyShip);
        }

        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}/ship-queue?page=1&pageSize=2");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var page = await result.ReadAsJsonAsync<PagedResponse<ShipBuildResponse>>();
        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalItems);
        Assert.True(page.HasNext);
    }

    [Fact]
    public async Task ShipRosterEndpointIsPaginatedAndInitiallyEmpty()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");

        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}/ships");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var page = await result.ReadAsJsonAsync<PagedResponse<RosterShipResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public async Task CancelQueuedShipRemovesIt()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");
        var build = await _host.QueueShip(registration, ShipType.ColonyShip);

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/ship-queue/{build.Id}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await _host.GetPlanet(registration);
        Assert.Equal(0, planet.QueueLength);
    }

    [Fact]
    public async Task CancelUnknownBuildReturns404()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/ship-queue/{Guid.NewGuid()}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task QueueShipOnUnownedPlanetReturns403()
    {
        var registration = await _host.RegisterPlayer("Ship_Test_");
        var foreign = await _host.FindPlanetOtherThan(registration);

        await _host.Scenario(s =>
        {
            s.Post.Json(new QueueShipRequest(ShipType.ColonyShip))
                .ToUrl($"/api/planets/{foreign}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }
}
