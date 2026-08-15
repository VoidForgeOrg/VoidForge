using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Colonize;

// #51 (phase-completion e2e, spec §7 item 5): the full loop the epic promises, on the
// REAL Wolverine scheduler (no handler-invoked shortcuts — see ClaimRaceTests/ColonizeMissionTests
// for those), stitched together in one flight: economy (homeworld already producing) -> ships
// (Shipyard, one Colony Ship + one Cargo Vessel) -> expand (Colonize an uncolonized planet in
// ANOTHER solar system, real scheduled arrival, colony owned + zero-store colony receives the
// exact loaded cargo + Colony Ship consumed) -> disband the surviving Cargo Vessel at the new
// colony -> supply the colony (build a SECOND Cargo Vessel at home, launch Transport to the
// colony -- an OWNED destination now, so this is the first true real-scheduler Transport e2e in
// the suite; TransportMissionEndToEndTests's Move round trip predates #51 and could only reach a
// FOREIGN planet) -> real scheduled arrival -> delivered, colony stores incremented exactly.
[Collection(IntegrationCollection.Name)]
public sealed class FullLoopEndToEndTests
{
    // Cargo amounts for each leg — small enough for a single Cargo Vessel's capacity (500,
    // Balance__CargoVessel), non-zero enough for the conservation assertions to mean something.
    // Kept identical to ClaimRaceTests'/ColonizeMissionTests' proven-safe values so the two
    // WaitForStock(150, 100) calls below reuse an already-validated recovery margin.
    private const decimal _colonizeCargoIronOre = 100m;
    private const decimal _colonizeCargoIronIngot = 50m;
    private const decimal _transportCargoIronOre = 100m;
    private const decimal _transportCargoIronIngot = 50m;

    private static readonly TimeSpan _arrivalTimeout = TimeSpan.FromSeconds(60);

    private readonly IAlbaHost _host;

    public FullLoopEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task EconomyToShipsToExpansionToSupplyingTheColonyCompletesTheFullLoop()
    {
        var settler = await RegisterPlayer();
        var homeworld = await GetPlanetById(settler, settler.HomeworldId);

        var destinationId = await ColonizeAnUncolonizedPlanetInAnotherSystem(settler, homeworld.SolarSystemId);
        await SupplyTheColonyViaTransport(settler, destinationId);
    }

    // Ships (Shipyard, one Colony Ship + one Cargo Vessel) -> expand: Colonize an uncolonized
    // planet in ANOTHER solar system on the real scheduler -> colony owned, zero-store colony
    // received the exact loaded cargo, Colony Ship consumed -> disband the surviving Cargo
    // Vessel at the new colony. Returns the colonized planet's id for the Transport leg.
    private async Task<Guid> ColonizeAnUncolonizedPlanetInAnotherSystem(RegisterPlayerResponse settler, Guid homeSystemId)
    {
        var colonyShipId = await BuildRosterShip(settler, ShipType.ColonyShip);
        var cargoVesselId = await BuildRosterShip(settler, ShipType.CargoVessel);
        await WaitForStock(settler, 150m, 100m);

        var colonizeFleet = await AssembleFleet(
            settler, [colonyShipId, cargoVesselId], new CargoRequest(_colonizeCargoIronOre, _colonizeCargoIronIngot));
        var destinationId = await UncolonizedPlanetInAnotherSystem(settler, homeSystemId);

        var launchedColonize = await Launch(settler, colonizeFleet.Id, MissionType.Colonize, destinationId);
        Assert.Equal(FleetStatus.InTransit, launchedColonize.Status);
        Assert.Equal(destinationId, launchedColonize.DestinationPlanetId);
        Assert.Equal(MissionType.Colonize, launchedColonize.Mission);

        var arrivedColonize = await PollFleetUntil(
            settler,
            colonizeFleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrivedColonize.Status);
        Assert.Equal(destinationId, arrivedColonize.LocationPlanetId);
        var survivingShip = Assert.Single(arrivedColonize.Ships);
        Assert.Equal(ShipType.CargoVessel, survivingShip.Type);   // the Colony Ship was consumed
        Assert.Equal(0m, arrivedColonize.CargoIronOre);           // auto-unloaded into the colony
        Assert.Equal(0m, arrivedColonize.CargoIronIngot);

        var colony = await GetPlanetById(settler, destinationId);
        Assert.Equal(settler.PlayerId, colony.OwnerId);
        // Planet.Claim seeds zero stores AND zero production rate (a fresh colony has no
        // buildings), so the exact-equality assertion is safe: nothing accrues between the
        // claim and this read.
        Assert.Equal(_colonizeCargoIronOre, colony.IronOre.CurrentValue);
        Assert.Equal(_colonizeCargoIronIngot, colony.IronIngot.CurrentValue);
        Assert.Equal(0m, colony.IronOre.Rate);
        Assert.Equal(0m, colony.IronIngot.Rate);

        // Disband the surviving Cargo Vessel at the new colony (cargo is already empty, so
        // D11's cargo-aboard guard doesn't block it).
        var disbanded = await Disband(settler, colonizeFleet.Id);
        Assert.Equal(FleetStatus.Disbanded, disbanded.Status);

        return destinationId;
    }

    // Supply the colony: a second Cargo Vessel built at home, Transport to the now-OWNED colony
    // on the real scheduler -- the first true real-scheduler Transport-to-own-planet round trip
    // in the suite (TransportMissionEndToEndTests predates #51 and could only reach a foreign
    // planet via Move). Still a zero-production colony, so exact-equality delivery math holds.
    private async Task SupplyTheColonyViaTransport(RegisterPlayerResponse settler, Guid destinationId)
    {
        var secondCargoVesselId = await BuildRosterShip(settler, ShipType.CargoVessel);
        await WaitForStock(settler, 150m, 100m);

        var transportFleet = await AssembleFleet(
            settler, [secondCargoVesselId], new CargoRequest(_transportCargoIronOre, _transportCargoIronIngot));

        var launchedTransport = await Launch(settler, transportFleet.Id, MissionType.Transport, destinationId);
        Assert.Equal(FleetStatus.InTransit, launchedTransport.Status);
        Assert.Equal(destinationId, launchedTransport.DestinationPlanetId);
        Assert.Equal(MissionType.Transport, launchedTransport.Mission);

        var arrivedTransport = await PollFleetUntil(
            settler,
            transportFleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrivedTransport.Status);
        Assert.Equal(destinationId, arrivedTransport.LocationPlanetId);
        Assert.Equal(0m, arrivedTransport.CargoIronOre);      // auto-delivered on arrival
        Assert.Equal(0m, arrivedTransport.CargoIronIngot);

        var colonyAfterTransport = await GetPlanetById(settler, destinationId);
        Assert.Equal(settler.PlayerId, colonyAfterTransport.OwnerId);
        Assert.Equal(_colonizeCargoIronOre + _transportCargoIronOre, colonyAfterTransport.IronOre.CurrentValue);
        Assert.Equal(_colonizeCargoIronIngot + _transportCargoIronIngot, colonyAfterTransport.IronIngot.CurrentValue);
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

    // Builds an operational shipyard (once per homeworld — reused across calls) and queues one
    // ship of the requested type, polling the roster until a ship absent from the pre-queue
    // roster snapshot appears. Mirrors ColonizeMissionTests/ClaimRaceTests' BuildRosterShip so a
    // homeworld can accumulate a Colony Ship, then a Cargo Vessel, then (after the first fleet's
    // ships have left the roster via assembly) a second Cargo Vessel across three calls.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration, ShipType type)
    {
        var before = (await GetRoster(registration)).Items.Select(i => i.Id).ToHashSet();

        await EnsureOperationalShipyard(registration);
        await QueueShip(registration, type);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            var roster = await GetRoster(registration);
            var added = roster.Items.Where(i => !before.Contains(i.Id)).ToList();
            if (added.Count > 0)
            {
                return added[0].Id;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
    }

    private async Task EnsureOperationalShipyard(RegisterPlayerResponse registration)
    {
        var planet = await GetPlanet(registration);
        if (!planet.Buildings.Any(b => b.Type == BuildingType.Shipyard))
        {
            await _host.Scenario(s =>
            {
                s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                    .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
                s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
                s.StatusCodeShouldBe(200);
            });
        }

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
    // Necessary because shipyard/ship construction (test-host drain rates) can crush the ingot
    // pool to near zero for several seconds before production recovers it.
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
        => await GetPlanetById(registration, registration.HomeworldId);

    // Finds an uncolonized planet in a DIFFERENT solar system than the caller's homeworld,
    // through the public solar-systems listing (the real player-visible read path, matching
    // ClaimRaceTests.UncolonizedPlanetId) rather than a raw store query. Relies on the
    // IntegrationCollection's serialized test execution (no concurrent writer between this scan
    // and the subsequent launches) — must not be copied into a parallel context.
    private async Task<Guid> UncolonizedPlanetInAnotherSystem(RegisterPlayerResponse asWhom, Guid homeSystemId)
    {
        var systems = await GetJson<PagedResponse<SolarSystemResponse>>(asWhom, "/api/solar-systems?pageSize=200");

        foreach (var system in systems.Items)
        {
            if (system.Id == homeSystemId)
            {
                continue;
            }

            foreach (var planetId in system.PlanetIds)
            {
                var planet = await GetPlanetById(asWhom, planetId);
                if (planet.OwnerId is null)
                {
                    return planet.Id;
                }
            }
        }

        throw new InvalidOperationException("No uncolonized planet found in another solar system across the listed solar systems.");
    }

    private async Task<PlanetResponse> GetPlanetById(RegisterPlayerResponse asWhom, Guid planetId)
        => await GetJson<PlanetResponse>(asWhom, $"/api/planets/{planetId}");

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
            s.Post.Json(new RegisterPlayerRequest($"FullLoopE2E_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
