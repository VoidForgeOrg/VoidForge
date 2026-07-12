using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Ships;

[Collection(IntegrationCollection.Name)]
public sealed class ShipConstructionCompletionTests
{
    private readonly IAlbaHost _host;

    public ShipConstructionCompletionTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task QueuedShipsBuildInParallelAndCompleteOntoRoster()
    {
        var registration = await RegisterPlayer();
        await BuildOperationalShipyard(registration);

        // Queue 4 ships: 3 start (capacity 3 for one shipyard), 1 waits.
        for (var i = 0; i < 4; i++)
        {
            await QueueShip(registration, ShipType.CargoVessel);
        }

        var afterQueue = await GetPlanet(registration);
        Assert.Equal(3, afterQueue.ActiveBuilds);
        Assert.Equal(1, afterQueue.QueueLength);

        // Wait for all four to complete (2s builds + scheduler poll; the 4th starts after one frees).
        var done = await PollUntil(registration, p => p.ShipCount == 4, TimeSpan.FromSeconds(40));
        Assert.Equal(4, done.ShipCount);
        Assert.Equal(0, done.ActiveBuilds);
        Assert.Equal(0, done.QueueLength);

        // Roster is readable and paginated.
        var roster = await GetRoster(registration);
        Assert.Equal(4, roster.TotalItems);
    }

    [Fact]
    public async Task CancellingAnActiveBuildAutoStartsTheQueuedOneAndStaleCompletionNoOps()
    {
        var registration = await RegisterPlayer();
        await BuildOperationalShipyard(registration);

        var builds = new List<ShipBuildResponse>();
        for (var i = 0; i < 4; i++)
        {
            builds.Add(await QueueShip(registration, ShipType.CargoVessel));
        }

        // Cancel one active build immediately (before it completes). The 4th (queued) auto-starts;
        // the cancelled build's already-scheduled CompleteShipConstruction later fires and no-ops
        // via validate-on-arrival — so the roster must never include the cancelled build.
        var active = builds.First(b => b.Status == ShipBuildStatus.Active);
        await CancelBuild(registration, active.Id);

        var done = await PollUntil(registration, p => p.ShipCount == 3 && p.ActiveBuilds == 0, TimeSpan.FromSeconds(40));
        Assert.Equal(3, done.ShipCount);        // 4 queued − 1 cancelled = 3 completed
        Assert.Equal(0, done.QueueLength);

        var roster = await GetRoster(registration);
        Assert.DoesNotContain(roster.Items, r => r.Id == active.Id);   // stale completion did not resurrect it
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

    private async Task CancelBuild(RegisterPlayerResponse registration, Guid buildId)
    {
        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/ship-queue/{buildId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
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
            s.Post.Json(new RegisterPlayerRequest($"ShipE2E_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
