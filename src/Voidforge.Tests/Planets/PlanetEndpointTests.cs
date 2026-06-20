using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Planets;

[Collection(IntegrationCollection.Name)]
public sealed class PlanetEndpointTests
{
    private readonly IAlbaHost _host;

    public PlanetEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task GetSolarSystemsReturnsSeededSystems()
    {
        var registration = await RegisterPlayer();

        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/solar-systems");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var systems = await result.ReadAsJsonAsync<List<SolarSystemResponse>>();
        Assert.NotNull(systems);
        Assert.NotEmpty(systems);
    }

    [Fact]
    public async Task GetSolarSystemsWithoutAuthReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/api/solar-systems");
            s.StatusCodeShouldBe(401);
        });
    }

    [Fact]
    public async Task GetPlanetByIdReturnsSeededPlanet()
    {
        var registration = await RegisterPlayer();

        var planetResult = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await planetResult.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);
        Assert.Equal(registration.HomeworldId, planet.Id);
        Assert.NotEqual(string.Empty, planet.Name);
        Assert.Equal(registration.PlayerId, planet.OwnerId);
        Assert.True(planet.IronOrePool > 0);
        Assert.True(planet.BuildingSlotCount > 0);
        Assert.True(planet.IronOre.StorageCapacity > 0);
        Assert.True(planet.IronIngot.StorageCapacity > 0);
        Assert.True(planet.IronOre.CurrentValue > 0);
        Assert.True(planet.IronIngot.CurrentValue > 0);
    }

    [Fact]
    public async Task GetPlanetByIdReturnsComputedValuesNotRawCheckpoint()
    {
        var registration = await RegisterPlayer();

        var planetResult = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await planetResult.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);

        // The homeworld starts with a Drill, so Iron Ore extracts at a positive rate.
        // The Refinery is inert in Phase 2, so Iron Ingots stay flat (rate 0).
        Assert.True(planet.IronOre.Rate > 0);
        Assert.Equal(0m, planet.IronIngot.Rate);
    }

    [Fact]
    public async Task GetPlanetByIdReturnsStartingBuildings()
    {
        var registration = await RegisterPlayer();

        var planetResult = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var planet = await planetResult.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);

        // Homeworld starts with 1 Drill, 1 Refinery, 1 Generator (issue #10).
        Assert.Equal(3, planet.Buildings.Count);
        Assert.Contains(planet.Buildings, b => b.Type == BuildingType.Drill);
        Assert.Contains(planet.Buildings, b => b.Type == BuildingType.Refinery);
        Assert.Contains(planet.Buildings, b => b.Type == BuildingType.Generator);
    }

    [Fact]
    public async Task GetPlanetByNonExistentIdReturns404()
    {
        var registration = await RegisterPlayer();

        await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{Guid.NewGuid()}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task GetPlanetWithoutAuthReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{Guid.NewGuid()}");
            s.StatusCodeShouldBe(401);
        });
    }

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Planet_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
