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
    public async Task OverloadThrottlesProductionProportionally()
    {
        var registration = await RegisterPlayer();

        // Homeworld draw 50 MW + Shipyard(40) + Shipyard(40) + Refinery(30) = 160 MW
        // vs 100 MW generation => m = 0.625 exactly. Now 2 refineries (demand 10) against
        // 1 drill (inflow 10): effective consumption = min(10,10) x 0.625 = 6.25.
        await PlaceBuilding(registration, BuildingType.Shipyard);
        await PlaceBuilding(registration, BuildingType.Shipyard);
        await PlaceBuilding(registration, BuildingType.Refinery);

        var planet = await GetPlanet(registration);

        Assert.Equal(0.625m, planet.Energy.ProductivityMultiplier);
        // Drill inflow (6.25) fully consumed by refineries => net ore 0.
        Assert.Equal(0m, planet.IronOre.Rate);
        // Ingots = 2 x 6.25 = 12.5 (throttled from the un-overloaded 20).
        Assert.Equal(12.5m, planet.IronIngot.Rate);
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
        // Two drills (inflow 20) minus the homeworld refinery (demand 5) => net ore 15.
        Assert.Equal(15m, recovered.IronOre.Rate);
    }

    [Fact]
    public async Task HomeworldRefineryProducesIngotsAtTwiceOreConsumption()
    {
        var registration = await RegisterPlayer();

        var first = await GetPlanet(registration);
        // Homeworld: drill inflow 10, refinery demand 5, m=1 => net ore +5/s, ingots +10/s.
        Assert.Equal(5m, first.IronOre.Rate);
        Assert.Equal(10m, first.IronIngot.Rate);

        await Task.Delay(1000);
        var second = await GetPlanet(registration);

        // Both pools rise (refineries convert the inflow, not the stored buffer, so net ore
        // stays positive); ingots climb at twice the effective ore consumption.
        Assert.True(second.IronIngot.CurrentValue > first.IronIngot.CurrentValue,
            $"Expected ingots to rise: {first.IronIngot.CurrentValue} -> {second.IronIngot.CurrentValue}");
        Assert.True(second.IronOre.CurrentValue > first.IronOre.CurrentValue,
            $"Expected net ore to rise: {first.IronOre.CurrentValue} -> {second.IronOre.CurrentValue}");
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
