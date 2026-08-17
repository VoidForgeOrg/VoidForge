using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Construction;

[Trait("Category", "Integration")]
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
        var registration = await _host.RegisterPlayer("Construct_Test_");
        var before = await _host.GetPlanet(registration);
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
        var operational = await _host.PollUntil(
            registration,
            p => p.Buildings[^1].Status == BuildingStatus.Operational,
            timeout: TestTimeouts.Completion);

        Assert.Equal(BuildingStatus.Operational, operational.Buildings[^1].Status);
        Assert.Null(operational.Buildings[^1].EtaCompletionUtc);
        // Completion switched on the second generator's output: 100 -> 200 MW.
        Assert.Equal(200m, operational.Energy.GenerationMw);
    }
}
