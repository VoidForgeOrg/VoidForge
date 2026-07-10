using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Energy;

[Collection(IntegrationCollection.Name)]
public sealed class EnergyGridTests
{
    private readonly IAlbaHost _host;

    public EnergyGridTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task HomeworldEnergyBlockReflectsStartingBuildings()
    {
        var registration = await RegisterPlayer();

        var planet = await GetPlanet(registration);

        // Starting composition: Generator (100 MW) vs Drill (20) + Refinery (30).
        Assert.Equal(100m, planet.Energy.GenerationMw);
        Assert.Equal(50m, planet.Energy.ConsumptionMw);
        Assert.Equal(1m, planet.Energy.ProductivityMultiplier);
    }

    [Fact]
    public async Task OverloadThrottlesDrillRateProportionally()
    {
        var registration = await RegisterPlayer();

        // Homeworld: 50 MW draw, 3 free slots. Add Shipyard + Shipyard + Refinery
        // => consumption 160 vs generation 100 => m = 0.625 exactly.
        await PlaceBuilding(registration, BuildingType.Shipyard);
        await PlaceBuilding(registration, BuildingType.Shipyard);
        await PlaceBuilding(registration, BuildingType.Refinery);

        var planet = await GetPlanet(registration);

        Assert.Equal(0.625m, planet.Energy.ProductivityMultiplier);
        // One drill at 10/s, throttled: 10 * 0.625.
        Assert.Equal(6.25m, planet.IronOre.Rate);
    }

    [Fact]
    public async Task AddingGeneratorRestoresProductivity()
    {
        var registration = await RegisterPlayer();

        // 50 + 40 + 20 = 110 MW draw vs 100 MW => overloaded.
        await PlaceBuilding(registration, BuildingType.Shipyard);
        await PlaceBuilding(registration, BuildingType.Drill);
        var overloaded = await GetPlanet(registration);
        Assert.True(overloaded.Energy.ProductivityMultiplier < 1m);
        Assert.True(overloaded.IronOre.Rate < 20m);

        // Second generator: 200 MW vs 110 MW => fully powered again.
        await PlaceBuilding(registration, BuildingType.Generator);
        var recovered = await GetPlanet(registration);
        Assert.Equal(1m, recovered.Energy.ProductivityMultiplier);
        Assert.Equal(20m, recovered.IronOre.Rate);
    }

    private async Task PlaceBuilding(RegisterPlayerResponse registration, BuildingType type)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
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

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Energy_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
