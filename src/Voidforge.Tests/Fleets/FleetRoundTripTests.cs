using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Fleets;

// Merge-gate e2e test: exercises the full fleet-assembly feature end to end against real
// scheduled ship completions (build -> assemble -> roster shrinks -> disband -> ships returned).
[Collection(IntegrationCollection.Name)]
public sealed class FleetRoundTripTests
{
    private readonly IAlbaHost _host;

    public FleetRoundTripTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ShipsRoundTripThroughAFleet()
    {
        var registration = await RegisterPlayer();
        await BuildOperationalShipyard(registration);
        var ship1 = await BuildRosterShip(registration);
        var ship2 = await BuildRosterShip(registration);

        var rosterBefore = await GetRoster(registration);
        Assert.Equal(2, rosterBefore.TotalItems);

        var fleet = await AssembleFleet(registration, [ship1, ship2]);
        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(2, fleet.Ships.Count);
        Assert.Equal(0, (await GetRoster(registration)).TotalItems);  // roster shrank

        await Disband(registration, fleet.Id);

        var rosterAfter = await GetRoster(registration);
        Assert.Equal(2, rosterAfter.TotalItems);                      // ships returned
        Assert.All(rosterAfter.Items, s => Assert.Equal(registration.PlayerId, s.OwnerId));
    }

    // Builds an operational shipyard, queues one CargoVessel (~2s build), and polls the
    // roster until a new ship appears. Returns the completed ship's id.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration)
    {
        var before = await GetRoster(registration);
        await QueueShip(registration, ShipType.CargoVessel);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            var roster = await GetRoster(registration);
            var newShip = roster.Items.FirstOrDefault(s => before.Items.All(b => b.Id != s.Id));
            if (newShip is not null)
            {
                return newShip.Id;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
    }

    private async Task<FleetResponse> AssembleFleet(RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds)).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    private async Task<FleetResponse> Disband(RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    private async Task BuildOperationalShipyard(RegisterPlayerResponse registration)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        await PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == BuildingType.Shipyard && b.Status == BuildingStatus.Operational),
            TimeSpan.FromSeconds(20));
    }

    private async Task<ShipBuildResponse> QueueShip(RegisterPlayerResponse registration, ShipType type)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new QueueShipRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var build = await result.ReadAsJsonAsync<ShipBuildResponse>();
        Assert.NotNull(build);
        return build;
    }

    private async Task<PagedResponse<RosterShipResponse>> GetRoster(RegisterPlayerResponse registration)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{registration.HomeworldId}/ships?pageSize=200");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var roster = await result.ReadAsJsonAsync<PagedResponse<RosterShipResponse>>();
        Assert.NotNull(roster);
        return roster;
    }

    private async Task<PlanetResponse> PollUntil(
        RegisterPlayerResponse registration, Func<PlanetResponse, bool> predicate, TimeSpan timeout)
    {
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

        return planet;
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
            s.Post.Json(new RegisterPlayerRequest($"FleetRT_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
