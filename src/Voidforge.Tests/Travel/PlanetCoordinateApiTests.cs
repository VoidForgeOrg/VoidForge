using Alba;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voidforge.Api.Auth;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Api.WorldGeneration;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Travel;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class PlanetCoordinateApiTests
{
    private readonly IAlbaHost _host;
    private readonly decimal _planetSpread;

    public PlanetCoordinateApiTests(AppFixture fixture)
    {
        _host = fixture.Host;
        _planetSpread = _host.Services.GetRequiredService<IOptions<WorldGenOptions>>().Value.PlanetSpread;
    }

    [Fact]
    public async Task PlanetCoordinatesAreWithinSpreadOfItsSolarSystem()
    {
        var registration = await _host.RegisterPlayer("Coord_Test_");

        var planetResult = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
        var planet = await planetResult.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);

        var systemsResult = await _host.Scenario(s =>
        {
            s.Get.Url("/api/solar-systems?pageSize=200");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
        var systems = await systemsResult.ReadAsJsonAsync<PagedResponse<SolarSystemResponse>>();
        Assert.NotNull(systems);

        var system = systems.Items.Single(s => s.Id == planet.SolarSystemId);

        Assert.InRange(planet.X, system.X - _planetSpread, system.X + _planetSpread);
        Assert.InRange(planet.Y, system.Y - _planetSpread, system.Y + _planetSpread);
        Assert.InRange(planet.Z, system.Z - _planetSpread, system.Z + _planetSpread);
    }
}
