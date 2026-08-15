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

        // #58: settle the arrangement against the homeworld's live-production stream contention
        // (see AssembleFleetSettled) so the ONLY contention the assertions below observe is the
        // intended two-disband race on the fleet + planet streams.
        var fleet = await AssembleFleetSettled(registration, [shipId]);

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

    // #58 deterministic arrangement. Assemble the fleet at the homeworld, retrying across a BENIGN
    // transient optimistic-concurrency 409 so a background collision can't fail the *arrangement*
    // (the reported flake was a non-200 thrown from an arrangement step, not the disband race).
    //
    // Mechanism: the homeworld is seeded with an Operational Drill + Refinery + Generator
    // (PlayerEndpoints.Register), whose live production makes registration schedule durable
    // CheckStorageFull / CheckPoolDepleted / CheckInputStarved messages. Under Solo durability those
    // fire on the wall clock throughout the test and append halt/resume events to the homeworld's
    // PLANET stream. Assemble also appends to that stream (ShipsRemovedFromRoster, via
    // FetchForWriting<Planet>), so under CI load it can lose the optimistic-concurrency guard to a
    // cascade check that commits inside its narrow fetch→commit window; that surfaces to the caller as
    // a transient 409 (ConcurrencyConflictExceptionHandler) — the duplicate-key retry storm #58 saw.
    //
    // The collision is benign: the ship is already settled on the roster and owned by the caller. The
    // homeworld OwnerId that Apply(ShipCompleted) stamps onto each RosterShip is set by PlanetColonized
    // at registration (before any ship or building exists) and is immutable in the MVP; inline snapshots
    // always apply events in stream order, so ShipCompleted can never observe a stale/absent OwnerId —
    // even under Quick append + ConcurrencyException retries (a retry re-runs against the committed
    // snapshot, which already carries the owner). The "stale OwnerId under Quick-append + retry" domain
    // race hypothesized in #58 is therefore ruled out; the flake is stream *contention*, not a
    // correctness bug. Retrying the assemble across the 409 removes that contention from the arrangement
    // without touching the disband race. A non-409 status (e.g. a genuine 403 ownership failure) is NOT
    // a benign concurrency outcome and fails loudly — #58 asks to understand, not suppress.
    private async Task<FleetResponse> AssembleFleetSettled(
        RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var result = await _host.Scenario(s =>
            {
                s.Post.Json(new AssembleFleetRequest(shipIds))
                    .ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
                s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
                s.IgnoreStatusCode();
            });

            var status = result.Context.Response.StatusCode;
            if (status == 200)
            {
                var fleet = await result.ReadAsJsonAsync<FleetResponse>();
                Assert.NotNull(fleet);
                return fleet;
            }

            // Only a transient optimistic-concurrency 409 is retryable; anything else is a real failure.
            Assert.True(
                status == 409 && attempt < maxAttempts,
                $"Assemble arrangement returned {status} on attempt {attempt}/{maxAttempts} " +
                "(only a transient 409 is retried).");
            await Task.Delay(TestTimeouts.PollInterval);
        }
    }
}
