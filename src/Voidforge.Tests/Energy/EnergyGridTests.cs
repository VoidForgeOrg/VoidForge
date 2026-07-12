using Alba;
using Voidforge.Api.Auth;
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

    // Overload throttling of the multiplier and of pool rates is covered by unit tests
    // (PlanetEnergyTests.OverloadScalesProductivityProportionally and
    // PlanetAggregateTests.ApplyBuildingPlacedOverloadRescalesExistingDrillRates). Since #26,
    // endpoint placement starts construction (UnderConstruction, no immediate operational
    // effect), so the former place-and-assert-overload integration tests were retired here;
    // the construction→operational→effects path is exercised end-to-end in
    // BuildingConstructionCompletionTests.

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
