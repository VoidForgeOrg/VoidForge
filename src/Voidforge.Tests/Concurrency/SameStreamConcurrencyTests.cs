using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Concurrency;

// Regression coverage for #39: concurrent appends to one Planet event stream must be safe under
// Marten optimistic concurrency + a Wolverine retry policy. Two collision surfaces are exercised:
//   - HTTP-vs-scheduled (case 2): player commands overlapping a scheduled completion.
//   - HTTP-vs-HTTP: concurrent player commands on the same planet.
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class SameStreamConcurrencyTests
{
    private readonly IAlbaHost _host;

    public SameStreamConcurrencyTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ConcurrentCommandsOnOnePlanetConflictAs409NotServerError()
    {
        var registration = await _host.RegisterPlayer("Concurrency_");

        // Fire batches of concurrent Queue commands at the same planet stream. With optimistic
        // concurrency a colliding append fails cleanly (409); without it the appends collide on the
        // Postgres stream-version unique constraint (23505) and surface as a 500. A conflict is
        // timing-dependent, so retry batches until one is observed — the assertions below never pass
        // on the unfixed code (a 500 appears, or no 409 ever does).
        var successes = 0;
        var sawConflict = false;
        var sawServerError = false;

        for (var round = 0; round < 20 && !sawConflict; round++)
        {
            var codes = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => QueueShipStatus(registration)));

            successes += codes.Count(c => c == 200);
            sawServerError |= codes.Contains(500);
            sawConflict |= codes.Contains(409);
        }

        Assert.False(sawServerError, "a concurrent-append conflict surfaced as 500 instead of 409");
        Assert.True(successes > 0, "expected at least one command to win the race");
        Assert.True(sawConflict, "expected a concurrent-append conflict to surface as 409");

        // Consistency: exactly the winning appends landed — no lost or duplicated writes. (No shipyard,
        // so every queued ship stays Queued and none complete.)
        var planet = await _host.GetPlanet(registration);
        Assert.Equal(successes, planet.QueueLength);
    }

    [Fact]
    public async Task HttpCommandsRacingAScheduledCompletionRemainConsistent()
    {
        var registration = await _host.RegisterPlayer("Concurrency_");

        // Start a Drill: its construction schedules a CompleteBuildingConstruction (~5s) on this
        // planet stream — the "race partner" for the HTTP appends below.
        await PlaceBuilding(registration, BuildingType.Drill);

        // For ~8s (spanning the completion's delivery window) hammer the SAME planet with queue-ship
        // commands. With no operational shipyard the ships just sit Queued — resource-neutral appends
        // that never start, drain, or complete — but each still races the scheduled building
        // completion on the stream version (#39 case 2). Losers get a clean 409, never a 500.
        var successes = 0;
        var sawServerError = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var codes = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => QueueShipStatus(registration)));
            successes += codes.Count(c => c == 200);
            sawServerError |= codes.Contains(500);
            await Task.Delay(100);
        }

        Assert.False(sawServerError, "an HTTP command surfaced a 500 instead of a clean 409");

        // Eventual consistency: the scheduled building completion was applied — never dropped by a
        // conflict (the handler retried) — and every winning queue append landed exactly once.
        var settled = await _host.PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == BuildingType.Drill && b.Status == BuildingStatus.Operational),
            TestTimeouts.StockRecovery);

        Assert.Contains(settled.Buildings, b => b.Type == BuildingType.Drill && b.Status == BuildingStatus.Operational);
        Assert.Equal(successes, settled.QueueLength);
    }

    private async Task PlaceBuilding(RegisterPlayerResponse registration, BuildingType type)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
    }

    // Fire a Queue command and return only its status code (no auto-assert).
    private Task<int> QueueShipStatus(RegisterPlayerResponse registration)
        => _host.PostForStatus(
            registration,
            $"/api/planets/{registration.HomeworldId}/ship-queue",
            new QueueShipRequest(ShipType.CargoVessel));
}
