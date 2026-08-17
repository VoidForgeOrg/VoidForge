using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Wolverine;
using Xunit;

namespace Voidforge.Tests.Buildings;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class BuildingEndpointTests
{
    private readonly IAlbaHost _host;

    public BuildingEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    // Places a Generator on the caller's homeworld and asserts the response status code.
    private async Task PlaceGenerator(RegisterPlayerResponse registration, int expectedStatus)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Generator))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(expectedStatus);
        });
    }

    // Free slots the planet will still accept a placement into: capacity minus LIVE (non-tombstone)
    // buildings. Raw Buildings.Count would overcount once Cancelled/Demolished tombstones linger in
    // the append-only list (#72), so filter them out — mirrors Planet.LiveBuildingCount server-side.
    private static int FreeSlots(PlanetResponse planet) => planet.BuildingSlotCount
        - planet.Buildings.Count(b => b.Status is not (BuildingStatus.Cancelled or BuildingStatus.Demolished));

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
        var freeSlots = FreeSlots(planet);
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

    [Fact]
    public async Task CancelConstructionReturns204AndTombstonesSlot()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var before = await _host.GetPlanet(registration);
        // The new slot lands at the raw list length (no tombstones yet) = its stable SlotIndex.
        var slotIndex = before.Buildings.Count;

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/buildings/{slotIndex}/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(204);
        });

        var after = await _host.GetPlanet(registration);
        Assert.Equal(BuildingStatus.Cancelled, after.Buildings[slotIndex].Status);
    }

    [Fact]
    public async Task CancelConstructionOnUnownedPlanetReturns403()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var foreignPlanetId = await _host.FindPlanetOtherThan(registration);

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{foreignPlanetId}/buildings/0/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task CancelConstructionOnNonExistentPlanetReturns404()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{Guid.NewGuid()}/buildings/0/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task CancelConstructionForOutOfRangeSlotReturns404()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/buildings/99/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task CancelConstructionOnOperationalBuildingReturns409()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        // Slot 0 is a seeded Operational homeworld building — only in-progress construction cancels.
        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/buildings/0/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task DemolishReturns202AndShutsBuildingDownImmediately()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var before = await _host.GetPlanet(registration);

        // The seeded homeworld Generator is Operational — demolishing it frees its energy at once.
        var slotIndex = Enumerable.Range(0, before.Buildings.Count).First(
            i => before.Buildings[i].Type == BuildingType.Generator
                && before.Buildings[i].Status == BuildingStatus.Operational);
        Assert.True(before.Energy.GenerationMw > 0m, "Precondition: homeworld generates energy.");

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{registration.HomeworldId}/buildings/{slotIndex}/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(202);
        });

        var after = await _host.GetPlanet(registration);
        var slot = after.Buildings[slotIndex];
        Assert.Equal(BuildingStatus.Demolishing, slot.Status);
        Assert.NotNull(slot.EtaCompletionUtc);
        // Immediate shutdown: the Generator left the Operational set, so generation drops right away.
        Assert.True(
            after.Energy.GenerationMw < before.Energy.GenerationMw,
            $"Demolished Generator should drop generation: before={before.Energy.GenerationMw}, after={after.Energy.GenerationMw}.");
    }

    [Fact]
    public async Task CompleteDemolitionTombstonesSlotAndFreesIt()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var planetId = registration.HomeworldId;
        var planet = await _host.GetPlanet(registration);

        // Fill every remaining slot so the planet is at capacity (FreeSlots counts non-tombstones).
        var freeSlots = FreeSlots(planet);
        for (var i = 0; i < freeSlots; i++)
        {
            await PlaceGenerator(registration, 200);
        }

        // At capacity: a placement is rejected.
        await PlaceGenerator(registration, 409);

        // Demolish slot 0 (a seeded Operational homeworld building) → 202, slot goes Demolishing.
        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{planetId}/buildings/0/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(202);
        });

        var demolishing = await _host.GetPlanet(registration);
        Assert.Equal(BuildingStatus.Demolishing, demolishing.Buildings[0].Status);
        var completesAt = demolishing.Buildings[0].EtaCompletionUtc;
        Assert.NotNull(completesAt);

        // Demolishing still OCCUPIES the slot, so the planet is still full — placement stays 409.
        await PlaceGenerator(registration, 409);

        // Drive the scheduled teardown directly at its predicted instant (deterministic — mirrors the
        // storage-halting tests) instead of waiting out DemolitionDurationSeconds by wall clock.
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = store.LightweightSession();
            await CompleteBuildingDemolitionHandler.Handle(
                new CompleteBuildingDemolition(planetId, 0, completesAt.Value), session, bus);
        }

        var after = await _host.GetPlanet(registration);
        Assert.Equal(BuildingStatus.Demolished, after.Buildings[0].Status);

        // The slot is freed: a placement now succeeds again.
        await PlaceGenerator(registration, 200);
    }

    [Fact]
    public async Task DemolishOnUnownedPlanetReturns403()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var foreignPlanetId = await _host.FindPlanetOtherThan(registration);

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{foreignPlanetId}/buildings/0/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task DemolishOnNonExistentPlanetReturns404()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{Guid.NewGuid()}/buildings/0/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task DemolishForOutOfRangeSlotReturns404()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{registration.HomeworldId}/buildings/99/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task DemolishUnderConstructionBuildingReturns409()
    {
        var registration = await _host.RegisterPlayer("Building_Test_");
        var before = await _host.GetPlanet(registration);
        var slotIndex = before.Buildings.Count;   // the new slot lands at the raw list length

        // Start a construction, then try to demolish it — only completed buildings can be demolished.
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Drill))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/planets/{registration.HomeworldId}/buildings/{slotIndex}/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }
}
