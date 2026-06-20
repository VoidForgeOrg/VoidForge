using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Buildings;

[Collection(IntegrationCollection.Name)]
public sealed class BuildingEndpointTests
{
    private readonly IAlbaHost _host;

    public BuildingEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task PlaceDrillIncreasesIronOreRateAdditively()
    {
        var registration = await RegisterPlayer();
        var before = await GetPlanet(registration);

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await result.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);
        Assert.Equal(before.Buildings.Count + 1, planet.Buildings.Count);
        // A second drill adds to the rate set by the starting drill.
        Assert.Equal(before.IronOre.Rate * 2, planet.IronOre.Rate);
    }

    [Fact]
    public async Task PlaceDrillThenIronOreIncreasesOverTime()
    {
        var registration = await RegisterPlayer();

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var first = await GetPlanet(registration);
        await Task.Delay(1000);
        var second = await GetPlanet(registration);

        Assert.True(
            second.IronOre.CurrentValue > first.IronOre.CurrentValue,
            $"Expected ore to increase: {first.IronOre.CurrentValue} -> {second.IronOre.CurrentValue}");
    }

    [Fact]
    public async Task PlaceRefineryDoesNotChangeIronOreRate()
    {
        var registration = await RegisterPlayer();
        var before = await GetPlanet(registration);

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Refinery))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await result.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);
        Assert.Equal(before.IronOre.Rate, planet.IronOre.Rate);
    }

    [Fact]
    public async Task PlaceBuildingOnUnownedPlanetReturns403()
    {
        var registration = await RegisterPlayer();
        var foreignPlanetId = await FindPlanetOtherThan(registration);

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{foreignPlanetId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task PlaceBuildingOnNonExistentPlanetReturns404()
    {
        var registration = await RegisterPlayer();

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{Guid.NewGuid()}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task PlaceBuildingWithoutAuthReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{Guid.NewGuid()}/buildings");
            s.StatusCodeShouldBe(401);
        });
    }

    [Fact]
    public async Task PlaceBuildingInOccupiedSlotsReturns409()
    {
        var registration = await RegisterPlayer();
        var planet = await GetPlanet(registration);

        // Fill remaining slots, then the next placement must be rejected.
        var freeSlots = planet.BuildingSlotCount - planet.Buildings.Count;
        for (var i = 0; i < freeSlots; i++)
        {
            await _host.Scenario(s =>
            {
                s.Post.Json(new PlaceBuildingRequest(BuildingType.Generator))
                    .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
                s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
                s.StatusCodeShouldBe(200);
            });
        }

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Generator))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(409);
        });
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

    private async Task<Guid> FindPlanetOtherThan(RegisterPlayerResponse registration)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/solar-systems");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var systems = await result.ReadAsJsonAsync<List<SolarSystemResponse>>();
        Assert.NotNull(systems);
        var other = systems.SelectMany(sys => sys.PlanetIds).First(id => id != registration.HomeworldId);
        return other;
    }

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Building_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
