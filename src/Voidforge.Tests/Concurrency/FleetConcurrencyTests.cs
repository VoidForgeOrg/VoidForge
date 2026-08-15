using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Tests.Support;
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
        var registration = await _host.RegisterPlayer("FleetConcurrency_");
        await _host.EnsureOperationalShipyard(registration);
        var shipId = await _host.BuildRosterShip(registration);

        var attempts = await Task.WhenAll(
            TryAssemble(registration, [shipId]),
            TryAssemble(registration, [shipId]));

        // #39 semantics: the loser either gets 409 (concurrency/roster conflict) — or,
        // if it read the already-mutated roster, a clean 409 not-on-roster. Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status == 409));

        var fleets = await _host.GetJson<PagedResponse<FleetSummaryResponse>>(registration, "/api/fleets");
        var fleet = Assert.Single(fleets.Items);
        Assert.Equal(1, fleet.ShipCount);
    }

    [Fact]
    public async Task ConcurrentLaunchesYieldExactlyOneDeparture()
    {
        var owner = await _host.RegisterPlayer("FleetConcurrency_");
        var beta = await _host.RegisterPlayer("FleetConcurrency_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        var attempts = await Task.WhenAll(
            TryLaunch(owner, fleet.Id, beta.HomeworldId),
            TryLaunch(owner, fleet.Id, beta.HomeworldId));

        // #39 semantics: the loser either collides at commit (409 concurrency) or, if it reads
        // the already-departed fleet, a clean 409 "only a stationed fleet can be launched".
        // Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status == 409));

        var fetched = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.InTransit, fetched.Status);
        Assert.NotNull(fetched.ArrivesAt);
    }

    [Fact]
    public async Task ConcurrentDisbandsOfTheSameFleetYieldExactlyOneSuccess()
    {
        var registration = await _host.RegisterPlayer("FleetConcurrency_");
        await _host.EnsureOperationalShipyard(registration);
        var shipId = await _host.BuildRosterShip(registration);
        var fleet = await _host.AssembleFleet(registration, [shipId]);

        var attempts = await Task.WhenAll(
            TryDisband(registration, fleet.Id),
            TryDisband(registration, fleet.Id));

        // #39 semantics: the loser either collides at commit (409 concurrency) or, if it reads
        // the already-disbanded fleet, a clean 409 "only a stationed fleet" — or a 404-family
        // outcome if it observes the stream after the winner's rewrite. Never two 200s.
        Assert.Equal(1, attempts.Count(status => status == 200));
        Assert.Equal(1, attempts.Count(status => status is 409 or 404));

        var roster = await _host.GetRoster(registration);
        Assert.Single(roster.Items, s => s.Id == shipId);
    }

    // Fire an Assemble command and return only its status code (no auto-assert).
    private Task<int> TryAssemble(RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds)
        => _host.PostForStatus(
            registration,
            $"/api/planets/{registration.HomeworldId}/fleets",
            new AssembleFleetRequest(shipIds));

    // Fire a Disband command and return only its status code (no auto-assert).
    // Stays hand-rolled: disband POSTs without a JSON body, which PostForStatus always sends.
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
    private Task<int> TryLaunch(RegisterPlayerResponse registration, Guid fleetId, Guid destinationPlanetId)
        => _host.PostForStatus(
            registration,
            $"/api/fleets/{fleetId}/missions",
            new LaunchMissionRequest(MissionType.Move, destinationPlanetId));
}
