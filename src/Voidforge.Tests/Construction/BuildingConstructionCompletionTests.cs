using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Construction;

[Collection(IntegrationCollection.Name)]
public sealed class BuildingConstructionCompletionTests
{
    private readonly IAlbaHost _host;

    public BuildingConstructionCompletionTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task PlacedGeneratorBecomesOperationalAndRaisesGeneration()
    {
        var registration = await RegisterPlayer();
        var before = await GetPlanet(registration);
        Assert.Equal(100m, before.Energy.GenerationMw);   // homeworld generator only

        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Generator))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        // Wolverine's scheduler polls ~every 5s; the build duration is short (test config).
        // Poll until the durable CompleteBuildingConstruction message fires and the slot
        // flips Operational.
        var operational = await PollUntil(
            registration,
            p => p.Buildings[^1].Status == BuildingStatus.Operational,
            timeout: TimeSpan.FromSeconds(20));

        Assert.Equal(BuildingStatus.Operational, operational.Buildings[^1].Status);
        Assert.Null(operational.Buildings[^1].EtaCompletionUtc);
        // Completion switched on the second generator's output: 100 -> 200 MW.
        Assert.Equal(200m, operational.Energy.GenerationMw);
    }

    private async Task<PlanetResponse> PollUntil(
        RegisterPlayerResponse registration, Func<PlanetResponse, bool> predicate, TimeSpan timeout)
    {
        // Test wall-clock timeout — unrelated to the app's injected TimeProvider.
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

        return planet;   // final state; the caller's assertions report the failure
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
            s.Post.Json(new RegisterPlayerRequest($"Construct_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
