using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Travel;

// Merge-gate e2e (#49): the real Wolverine scheduler, not a manually-invoked handler
// (that path is HandlerInvokedArrivalStationsTheFleetAndIsIdempotent in
// FleetMissionEndpointTests). AppFixture overrides both ship speeds to 1000 units/s, so even
// a cross-system trip (world seeded with CoordinateRange 1000 → at most ~3500 units) resolves
// in a few seconds of simulated travel time; the poll timeout below is generous mostly to
// absorb Wolverine's scheduled-message poller latency, not the travel itself.
[Collection(IntegrationCollection.Name)]
public sealed class MoveMissionEndToEndTests
{
    private static readonly TimeSpan _arrivalTimeout = TimeSpan.FromSeconds(30);

    private readonly IAlbaHost _host;

    public MoveMissionEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchedFleetTravelsViaTheRealSchedulerAndArrivesStationedWithItsShipOnTheDestinationRoster()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        var homeworld = await GetPlanetById(owner, owner.HomeworldId);
        var destinationPlanetId = await PickPlanetInAnotherSolarSystem(owner, homeworld.SolarSystemId);

        var launched = await Launch(owner, fleet.Id, MissionType.Move, destinationPlanetId);

        // Trivially observable mid-flight: nothing has had time to arrive yet.
        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Null(launched.LocationPlanetId);
        Assert.Equal(destinationPlanetId, launched.DestinationPlanetId);

        var arrived = await PollFleetUntil(
            owner,
            fleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationPlanetId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationPlanetId, arrived.LocationPlanetId);
        Assert.Null(arrived.OriginPlanetId);
        Assert.Null(arrived.DestinationPlanetId);
        Assert.Null(arrived.Mission);
        Assert.Null(arrived.DepartedAt);
        Assert.Null(arrived.ArrivesAt);

        await Disband(owner, fleet.Id);

        var destinationRoster = await GetRosterAt(owner, destinationPlanetId);
        Assert.Contains(destinationRoster.Items, s => s.Id == shipId);
    }

    private async Task<FleetResponse> Launch(
        RegisterPlayerResponse registration, Guid fleetId, MissionType mission, Guid destinationPlanetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(mission, destinationPlanetId)).ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(response);
        return response;
    }

    private async Task Disband(RegisterPlayerResponse registration, Guid fleetId)
    {
        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
    }

    private async Task<FleetResponse> PollFleetUntil(
        RegisterPlayerResponse registration, Guid fleetId, Func<FleetResponse, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        FleetResponse fleet;
        do
        {
            fleet = await GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
            if (predicate(fleet))
            {
                return fleet;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return fleet;
    }

    // Picks the first planet belonging to a solar system other than the homeworld's — this
    // maximizes travel distance (still only seconds at the fixture's fast test speed) and
    // exercises the coordinate-driven planner across systems, not just within one.
    private async Task<Guid> PickPlanetInAnotherSolarSystem(RegisterPlayerResponse registration, Guid homeSolarSystemId)
    {
        var systems = await GetJson<PagedResponse<SolarSystemResponse>>(registration, "/api/solar-systems?pageSize=200");
        var other = systems.Items.FirstOrDefault(s => s.Id != homeSolarSystemId && s.PlanetIds.Count > 0);
        if (other is null)
        {
            throw new InvalidOperationException("No solar system other than the homeworld's was found among the seeded world.");
        }

        return other.PlanetIds[0];
    }

    private async Task<PlanetResponse> GetPlanetById(RegisterPlayerResponse registration, Guid planetId)
        => await GetJson<PlanetResponse>(registration, $"/api/planets/{planetId}");

    // Builds an operational shipyard, queues one CargoVessel (~2s build), and polls the
    // roster until it appears. Returns the completed ship's id.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration)
    {
        await BuildOperationalShipyard(registration);
        await QueueShip(registration, ShipType.CargoVessel);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            var roster = await GetRosterAt(registration, registration.HomeworldId);
            if (roster.Items.Count > 0)
            {
                return roster.Items[0].Id;
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

    private async Task<T> GetJson<T>(RegisterPlayerResponse registration, string url)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<T>();
        Assert.NotNull(response);
        return response;
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

    private async Task<PagedResponse<RosterShipResponse>> GetRosterAt(RegisterPlayerResponse registration, Guid planetId)
        => await GetJson<PagedResponse<RosterShipResponse>>(registration, $"/api/planets/{planetId}/ships?pageSize=200");

    private async Task<PlanetResponse> PollUntil(
        RegisterPlayerResponse registration, Func<PlanetResponse, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        PlanetResponse planet;
        do
        {
            planet = await GetPlanetById(registration, registration.HomeworldId);
            if (predicate(planet))
            {
                return planet;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return planet;
    }

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"MoveE2E_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
