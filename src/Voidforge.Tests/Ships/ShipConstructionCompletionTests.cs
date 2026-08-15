using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Ships;

[Collection(IntegrationCollection.Name)]
public sealed class ShipConstructionCompletionTests
{
    private readonly IAlbaHost _host;

    public ShipConstructionCompletionTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task QueuedShipsBuildInParallelAndCompleteOntoRoster()
    {
        var registration = await _host.RegisterPlayer("ShipE2E_");
        await _host.EnsureOperationalShipyard(registration);

        // Queue 4 ships: 3 start (capacity 3 for one shipyard), 1 waits.
        for (var i = 0; i < 4; i++)
        {
            await _host.QueueShip(registration, ShipType.CargoVessel);
        }

        var afterQueue = await _host.GetPlanet(registration);
        Assert.Equal(3, afterQueue.ActiveBuilds);
        Assert.Equal(1, afterQueue.QueueLength);

        // Wait for all four to complete (2s builds + scheduler poll; the 4th starts after one frees).
        var done = await _host.PollUntil(registration, p => p.ShipCount == 4, TestTimeouts.QueueDrain);
        Assert.Equal(4, done.ShipCount);
        Assert.Equal(0, done.ActiveBuilds);
        Assert.Equal(0, done.QueueLength);

        // Roster is readable and paginated.
        var roster = await _host.GetRoster(registration);
        Assert.Equal(4, roster.TotalItems);
    }

    [Fact]
    public async Task CancellingAnActiveBuildAutoStartsTheQueuedOneAndStaleCompletionNoOps()
    {
        var registration = await _host.RegisterPlayer("ShipE2E_");
        await _host.EnsureOperationalShipyard(registration);

        var builds = new List<ShipBuildResponse>();
        for (var i = 0; i < 4; i++)
        {
            builds.Add(await _host.QueueShip(registration, ShipType.CargoVessel));
        }

        // Cancel one active build immediately (before it completes). The 4th (queued) auto-starts;
        // the cancelled build's already-scheduled CompleteShipConstruction later fires and no-ops
        // via validate-on-arrival — so the roster must never include the cancelled build.
        var active = builds.First(b => b.Status == ShipBuildStatus.Active);
        await CancelBuild(registration, active.Id);

        var done = await _host.PollUntil(registration, p => p.ShipCount == 3 && p.ActiveBuilds == 0, TestTimeouts.QueueDrain);
        Assert.Equal(3, done.ShipCount);        // 4 queued − 1 cancelled = 3 completed
        Assert.Equal(0, done.QueueLength);

        var roster = await _host.GetRoster(registration);
        Assert.DoesNotContain(roster.Items, r => r.Id == active.Id);   // stale completion did not resurrect it
    }

    private async Task CancelBuild(RegisterPlayerResponse registration, Guid buildId)
    {
        await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/planets/{registration.HomeworldId}/ship-queue/{buildId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
    }
}
