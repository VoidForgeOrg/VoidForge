using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
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
    private static readonly TimeSpan _arrivalTimeout = TimeSpan.FromSeconds(30);

    private readonly IAlbaHost _host;

    public TransportMissionEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task CargoRidesAMoveRoundTripThenManualUnloadRestoresHomeStorage()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();   // a real, owned "foreign" planet to Move to and back from
        var shipId = await BuildRosterShip(owner);
        // Shipyard/ship construction drains ingots hard in the test host — wait for stock to
        // clear a safety margin above what's about to be loaded (mirrors CargoEndpointTests).
        await WaitForStock(owner, 150m, 100m);

        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        var afterAssemble = await GetPlanet(owner);

        // Outbound leg: real scheduler, cargo rides along untouched (Move never auto-delivers).
        await LaunchAndAwaitStationedAt(owner, fleet.Id, foreign.HomeworldId);

        // Return leg: real scheduler again, cargo still untouched.
        await LaunchAndAwaitStationedAt(owner, fleet.Id, owner.HomeworldId);

        // Manual unload at home: stationed, owner owns both the fleet and the planet, cargo
        // aboard — every guard on POST /api/fleets/{id}/unload is satisfied.
        var unloaded = await Unload(owner, fleet.Id);
        Assert.Equal(0m, unloaded.CargoIronOre);
        Assert.Equal(0m, unloaded.CargoIronIngot);

        // Home storage restored, robust to background production: two real flights plus
        // polling overhead means far more time elapses than the tight instant-call tolerances
        // used elsewhere (e.g. CargoEndpointTests), so production alone could easily exceed a
        // symmetric +/- band. Since nothing drains either pool once the roster ship's build
        // completes (no active construction, shipyard idle), the only sound assertion is a
        // lower bound on the delta: the loaded amounts came back, plus whatever accrued.
        var afterUnload = await GetPlanet(owner);
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
        var launched = await Launch(registration, fleetId, MissionType.Move, destinationPlanetId);
        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(100m, launched.CargoIronOre);
        Assert.Equal(50m, launched.CargoIronIngot);

        var arrived = await PollFleetUntil(
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

    private async Task<FleetResponse> Unload(RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/unload");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
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

    private async Task<FleetResponse> AssembleFleet(
        RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds, CargoRequest? cargo = null)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds, cargo)).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    // Builds an operational shipyard, queues one CargoVessel (~2s build in the test host), and
    // polls the roster until it appears. Returns the completed ship's id.
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

    // Waits for the homeworld's stored ore/ingot to reach at least the given amounts.
    // Necessary because shipyard/ship construction (test-host drain rates) can crush the
    // ingot pool to near zero for several seconds before production recovers it.
    private async Task WaitForStock(RegisterPlayerResponse registration, decimal minOre, decimal minIngot)
    {
        var planet = await PollUntil(
            registration,
            p => p.IronOre.CurrentValue >= minOre && p.IronIngot.CurrentValue >= minIngot,
            TimeSpan.FromSeconds(30));

        Assert.True(
            planet.IronOre.CurrentValue >= minOre && planet.IronIngot.CurrentValue >= minIngot,
            $"Stock did not recover in time: ore={planet.IronOre.CurrentValue} (need {minOre}), " +
            $"ingot={planet.IronIngot.CurrentValue} (need {minIngot}).");
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
        => await GetJson<PlanetResponse>(registration, $"/api/planets/{registration.HomeworldId}");

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

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"TransportE2E_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
