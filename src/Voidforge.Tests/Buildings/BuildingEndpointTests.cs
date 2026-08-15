using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
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
    public async Task PlaceBuildingStartsConstruction()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var before = await _host.GetPlanet(registration);

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

        var newSlot = planet.Buildings[^1];
        Assert.Equal(BuildingStatus.UnderConstruction, newSlot.Status);
        Assert.NotNull(newSlot.EtaCompletionUtc);
        // The under-construction drill does not yet extract ore (homeworld drill only).
        Assert.Equal(before.IronOre.Rate, planet.IronOre.Rate);
        // Construction drains ingots: the ingot rate drops below the homeworld's +10/s.
        Assert.True(planet.IronIngot.Rate < before.IronIngot.Rate,
            $"Expected ingot rate to drop for construction drain: {before.IronIngot.Rate} -> {planet.IronIngot.Rate}");
    }

    [Fact]
    public async Task UnderConstructionBuildingDrainsIngotsOverTime()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var first = await _host.GetPlanet(registration);
        Assert.Equal(BuildingStatus.UnderConstruction, first.Buildings[^1].Status);
        await Task.Delay(1000);
        var second = await _host.GetPlanet(registration);

        // With the short test build durations, drain exceeds the homeworld's +10/s ingot
        // production, so the stored ingot value falls while UnderConstruction.
        Assert.True(second.IronIngot.CurrentValue < first.IronIngot.CurrentValue,
            $"Expected ingots to fall under construction drain: {first.IronIngot.CurrentValue} -> {second.IronIngot.CurrentValue}");
    }

    [Fact]
    public async Task PlaceBuildingOnUnownedPlanetReturns403()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var foreignPlanetId = await _host.FindPlanetOtherThan(registration);

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
        var registration = await _host.RegisterPlayer("Building_Test_");

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
        var registration = await _host.RegisterPlayer("Building_Test_");
        var planet = await _host.GetPlanet(registration);

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
}
