using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Concurrency;

// Carry-over gate from #48's final review: the fleet endpoints' concurrency behavior had zero
// direct race coverage. The mechanism under test already exists (#39 optimistic concurrency +
// retry; FetchForWriting everywhere) — this closes the gap. Mirrors the batching idiom in
// SameStreamConcurrencyTests: fire concurrent requests without an auto-assert and capture the
// raw competing status codes.
[Collection(IntegrationCollection.Name)]
public sealed class FleetConcurrencyTests
{
    private readonly IAlbaHost _host;

    public FleetConcurrencyTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ConcurrentAssemblesOfTheSameShipYieldExactlyOneFleet()
    {
        var registration = await RegisterPlayer();
        await BuildOperationalShipyard(registration);
        var shipId = await BuildRosterShip(registration);

        var attempts = await Task.WhenAll(
            TryAssemble(registration, [shipId]),
            TryAssemble(registration, [shipId]));

        // #39 semantics: the loser either gets 409 (concurrency/roster conflict) — or,
        // if it read the already-mutated roster, a clean 409 not-on-roster. Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status == 409));

        var fleets = await GetJson<PagedResponse<FleetSummaryResponse>>(registration, "/api/fleets");
        var fleet = Assert.Single(fleets.Items);
        Assert.Equal(1, fleet.ShipCount);
    }

    [Fact]
    public async Task ConcurrentLaunchesYieldExactlyOneDeparture()
    {
        var owner = await RegisterPlayer();
        var beta = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        var attempts = await Task.WhenAll(
            TryLaunch(owner, fleet.Id, beta.HomeworldId),
            TryLaunch(owner, fleet.Id, beta.HomeworldId));

        // #39 semantics: the loser either collides at commit (409 concurrency) or, if it reads
        // the already-departed fleet, a clean 409 "only a stationed fleet can be launched".
        // Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status == 409));

        var fetched = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.InTransit, fetched.Status);
        Assert.NotNull(fetched.ArrivesAt);
    }

    [Fact]
    public async Task ConcurrentDisbandsOfTheSameFleetYieldExactlyOneSuccess()
    {
        var registration = await RegisterPlayer();
        await BuildOperationalShipyard(registration);
        var shipId = await BuildRosterShip(registration);
        var fleet = await AssembleFleet(registration, [shipId]);

        var attempts = await Task.WhenAll(
            TryDisband(registration, fleet.Id),
            TryDisband(registration, fleet.Id));

        // #39 semantics: the loser either collides at commit (409 concurrency) or, if it reads
        // the already-disbanded fleet, a clean 409 "only a stationed fleet" — or a 404-family
        // outcome if it observes the stream after the winner's rewrite. Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status is 409 or 404));

        var roster = await GetRoster(registration);
        Assert.Single(roster.Items, s => s.Id == shipId);
    }

    // Fire an Assemble command and return only its status code (no auto-assert).
    private async Task<int> TryAssemble(RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds)).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
    }

    // Fire a Disband command and return only its status code (no auto-assert).
    private async Task<int> TryDisband(RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
    }

    // Fire a Launch (Move) command and return only its status code (no auto-assert).
    private async Task<int> TryLaunch(RegisterPlayerResponse registration, Guid fleetId, Guid destinationPlanetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, destinationPlanetId))
                .ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
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

    // Builds an operational shipyard, queues one CargoVessel (~2s build), and polls the
    // roster until it appears. Returns the completed ship's id.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration)
    {
        await BuildOperationalShipyard(registration);
        await QueueShip(registration, ShipType.CargoVessel);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            var roster = await GetRoster(registration);
            if (roster.Items.Count > 0)
            {
                return roster.Items[0].Id;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
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
            s.Post.Json(new RegisterPlayerRequest($"FleetConcurrency_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
