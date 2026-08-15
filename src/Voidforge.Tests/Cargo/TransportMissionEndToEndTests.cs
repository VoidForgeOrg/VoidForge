using Alba;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Cargo;

// Merge-gate e2e (#50, spec §2.3-§2.5): the real Wolverine scheduler, not the
// manually-invoked-handler idiom used elsewhere in Cargo/ (TransportMissionEndpointTests'
// LaunchAndArriveInstantly). A real Transport-to-own-planet round trip needs a SECOND OWNED
// planet, which does not exist until #51 (colonization) lands — registration grants exactly
// one homeworld, and Transport's own launch guard requires the destination be owned by the
// caller. Until then, this is the real-scheduler proof for cargo: assemble-with-cargo at the
// homeworld -> launch Move to a foreign (another player's) planet, cargo riding along
// untouched (spec §2.4: Move never auto-delivers, unlike Transport/Colonize) -> real scheduled
// arrival -> cargo still intact -> launch Move back home -> real scheduled arrival -> manual
// unload (POST /api/fleets/{id}/unload) -> home storage restored. Transport's actual delivery
// math (accepted-amount headroom clamping, partial delivery when destination storage is full,
// the cross-aggregate append) is proven handler-invoked in TransportMissionEndpointTests;
// #51's plan should add the true real-scheduler Transport-to-own-planet round trip once a
// second owned planet is reachable via the API.
[Collection(IntegrationCollection.Name)]
public sealed class TransportMissionEndToEndTests
{
    private static readonly TimeSpan _arrivalTimeout = TestTimeouts.Arrival;

    private readonly IAlbaHost _host;

    public TransportMissionEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task CargoRidesAMoveRoundTripThenManualUnloadRestoresHomeStorage()
    {
        var owner = await _host.RegisterPlayer("TransportE2E_");
        var foreign = await _host.RegisterPlayer("TransportE2E_");   // a real, owned "foreign" planet to Move to and back from
        var shipId = await _host.BuildRosterShip(owner);
        // Shipyard/ship construction drains ingots hard in the test host — wait for stock to
        // clear a safety margin above what's about to be loaded (mirrors CargoEndpointTests).
        await _host.WaitForStock(owner, 150m, 100m);

        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        var afterAssemble = await _host.GetPlanet(owner);

        // Outbound leg: real scheduler, cargo rides along untouched (Move never auto-delivers).
        await LaunchAndAwaitStationedAt(owner, fleet.Id, foreign.HomeworldId);

        // Return leg: real scheduler again, cargo still untouched.
        await LaunchAndAwaitStationedAt(owner, fleet.Id, owner.HomeworldId);

        // Manual unload at home: stationed, owner owns both the fleet and the planet, cargo
        // aboard — every guard on POST /api/fleets/{id}/unload is satisfied.
        var unloaded = await _host.Unload(owner, fleet.Id);
        Assert.Equal(0m, unloaded.CargoIronOre);
        Assert.Equal(0m, unloaded.CargoIronIngot);

        // Home storage restored, robust to background production: two real flights plus
        // polling overhead means far more time elapses than the tight instant-call tolerances
        // used elsewhere (e.g. CargoEndpointTests), so production alone could easily exceed a
        // symmetric +/- band. Since nothing drains either pool once the roster ship's build
        // completes (no active construction, shipyard idle), the only sound assertion is a
        // lower bound on the delta: the loaded amounts came back, plus whatever accrued.
        var afterUnload = await _host.GetPlanet(owner);
        Assert.True(
            afterUnload.IronOre.CurrentValue - afterAssemble.IronOre.CurrentValue >= 100m,
            $"Expected at least the 100 ore that was unloaded back: before={afterAssemble.IronOre.CurrentValue}, " +
            $"after={afterUnload.IronOre.CurrentValue}.");
        Assert.True(
            afterUnload.IronIngot.CurrentValue - afterAssemble.IronIngot.CurrentValue >= 50m,
            $"Expected at least the 50 ingot that was unloaded back: before={afterAssemble.IronIngot.CurrentValue}, " +
            $"after={afterUnload.IronIngot.CurrentValue}.");
    }

    // Launches a Move to destinationPlanetId via the real scheduler and polls until the fleet
    // is Stationed there, asserting cargo rode along untouched both immediately after launch
    // and after the real arrival — Move's arrival path never delivers (spec §2.4).
    private async Task<FleetResponse> LaunchAndAwaitStationedAt(
        RegisterPlayerResponse registration, Guid fleetId, Guid destinationPlanetId)
    {
        var launched = await _host.Launch(registration, fleetId, MissionType.Move, destinationPlanetId);
        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(100m, launched.CargoIronOre);
        Assert.Equal(50m, launched.CargoIronIngot);

        var arrived = await _host.PollFleetUntil(
            registration,
            fleetId,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationPlanetId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationPlanetId, arrived.LocationPlanetId);
        Assert.Equal(100m, arrived.CargoIronOre);
        Assert.Equal(50m, arrived.CargoIronIngot);
        return arrived;
    }
}
