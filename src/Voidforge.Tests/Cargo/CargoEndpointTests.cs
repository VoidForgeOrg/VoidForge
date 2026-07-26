using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Cargo;

// Task 4 (#50, spec §2.3/§4/§5): cargo loaded at assembly + the manual unload endpoint.
// Amounts are chosen well under the homeworld's starting stock (500 ore / 100 ingots,
// see WorldGenOptions) for happy paths, and comfortably below a fleet's cargo capacity but
// above any realistic accrual for the "insufficient stored" 409.
[Collection(IntegrationCollection.Name)]
public sealed class CargoEndpointTests
{
    private readonly IAlbaHost _host;

    public CargoEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task AssembleWithCargoReturns200AndDecrementsOriginStorage()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        // Shipyard/ship construction drains ingots hard (test-host drain rates exceed the
        // homeworld's +10/s production) — wait for it to recover past what we're about to
        // load, plus margin, before taking the "before" snapshot.
        var before = await WaitForStock(owner, 150m, 100m);

        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));

        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        Assert.Equal(500m, fleet.CargoCapacity);   // one CargoVessel, spec §6 default

        var after = await GetPlanet(owner);
        // Production continues (Drill/Refinery) between reads, so allow slack either side
        // of the exact 100/50 the load subtracted.
        Assert.InRange(before.IronOre.CurrentValue - after.IronOre.CurrentValue, 90m, 130m);
        Assert.InRange(before.IronIngot.CurrentValue - after.IronIngot.CurrentValue, 30m, 80m);
    }

    [Fact]
    public async Task AssembleWithNoCargoLeavesFleetEmptyAndSkipsValidation()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);

        var fleet = await AssembleFleet(owner, [shipId]);

        Assert.Equal(0m, fleet.CargoIronOre);
        Assert.Equal(0m, fleet.CargoIronIngot);
    }

    [Fact]
    public async Task AssembleCargoExceedingCapacityReturns400()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);   // one CargoVessel: capacity 500

        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([shipId], new CargoRequest(300m, 300m)))
                .ToUrl($"/api/planets/{owner.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task AssembleWithNegativeCargoAmountReturns400()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);

        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([shipId], new CargoRequest(-10m, 0m)))
                .ToUrl($"/api/planets/{owner.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task AssembleCargoOnForeignPlanetReturns403EvenThoughShipsAreOwned()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);   // no cargo yet

        // Move (cargo-free) to the foreign homeworld, resolved instantly via the
        // handler-invoked pattern (avoids waiting on the real scheduled arrival).
        await MoveAndArriveInstantly(owner, fleet.Id, foreign.HomeworldId);
        await Disband(owner, fleet.Id);   // ships land on the foreign planet's roster (D13: still owner-owned)

        // Re-assemble on the foreign planet: ship ownership passes (owner still owns the
        // ships), but the cargo request requires owning the planet too.
        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest([shipId], new CargoRequest(10m, 0m)))
                .ToUrl($"/api/planets/{foreign.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    // D13 regression (carry-over from Task 4's review): assembling WITHOUT cargo only
    // validates ship ownership, never planet ownership — the inverse of
    // AssembleCargoOnForeignPlanetReturns403EvenThoughShipsAreOwned above, which requests
    // cargo and is correctly refused. Ships owned by the caller but stranded on a foreign
    // planet's roster must still be re-assemblable there when no cargo is requested.
    [Fact]
    public async Task AssembleWithoutCargoOnForeignPlanetReturns200()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);   // no cargo yet

        // Move (cargo-free) to the foreign homeworld, resolved instantly via the
        // handler-invoked pattern (avoids waiting on the real scheduled arrival).
        await MoveAndArriveInstantly(owner, fleet.Id, foreign.HomeworldId);
        await Disband(owner, fleet.Id);   // ships land on the foreign planet's roster (D13: still owner-owned)

        // The roster is on the foreign planet now, not owner's own homeworld.
        var reassembled = await AssembleFleet(owner, [shipId], planetId: foreign.HomeworldId);

        Assert.Equal(0m, reassembled.CargoIronOre);
        Assert.Equal(0m, reassembled.CargoIronIngot);
        Assert.Equal(foreign.HomeworldId, reassembled.LocationPlanetId);
    }

    [Fact]
    public async Task AssembleCargoExceedingStoredAmountReturns409()
    {
        var owner = await RegisterPlayer();
        var shipIds = await BuildRosterShips(owner, 2);   // two CargoVessels: capacity 1000

        // 900 ingots is comfortably under the 1000 capacity ceiling but far above anything
        // the homeworld could have accrued (starts at 100, +10/s net) during test setup.
        await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds, new CargoRequest(0m, 900m)))
                .ToUrl($"/api/planets/{owner.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task UnloadWithNoCargoAboardReturns409()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);   // no cargo

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/unload");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    // Regression: FleetEndpoints.Disband previously had no endpoint-level D11 check — it relied
    // on Fleet.Disband's own guard throwing InvalidOperationException, which nothing caught, so
    // this surfaced as an unhandled 500 rather than the spec's 409. Found while verifying the
    // D11 guard for this task's docs update; fixed alongside.
    [Fact]
    public async Task DisbandWithCargoAboardReturns409()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task UnloadAtAForeignPlanetReturns403()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(50m, 0m));

        // Cargo rides along on a Move (spec §2.4); resolved instantly.
        await MoveAndArriveInstantly(owner, fleet.Id, foreign.HomeworldId);

        await _host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleet.Id}/unload");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task AssembleThenUnloadRoundTripRestoresOriginStorage()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var beforeAssemble = await WaitForStock(owner, 150m, 100m);

        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var afterAssemble = await GetPlanet(owner);
        Assert.True(afterAssemble.IronOre.CurrentValue < beforeAssemble.IronOre.CurrentValue);

        var unloaded = await Unload(owner, fleet.Id);
        Assert.Equal(0m, unloaded.CargoIronOre);
        Assert.Equal(0m, unloaded.CargoIronIngot);

        var afterUnload = await GetPlanet(owner);
        // The 100/50 that left comes back (net of whatever accrued meanwhile).
        Assert.InRange(afterUnload.IronOre.CurrentValue - afterAssemble.IronOre.CurrentValue, 90m, 130m);
        Assert.InRange(afterUnload.IronIngot.CurrentValue - afterAssemble.IronIngot.CurrentValue, 30m, 80m);
    }

    private async Task MoveAndArriveInstantly(RegisterPlayerResponse registration, Guid fleetId, Guid destinationPlanetId)
    {
        var launched = await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Move, destinationPlanetId))
                .ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await launched.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(response);
        Assert.NotNull(response.ArrivesAt);

        // Handler-invoked resolution (spec §7 testing strategy item 2) — verifies the
        // handler → stream append path without waiting on the real scheduled envelope.
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleetId, response.ArrivesAt.Value), session);
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

    // planetId defaults to the registration's own homeworld; callers reassembling a roster
    // stranded on a foreign planet (D13) pass that planet's id explicitly instead.
    private async Task<FleetResponse> AssembleFleet(
        RegisterPlayerResponse registration, IReadOnlyList<Guid> shipIds, CargoRequest? cargo = null, Guid? planetId = null)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds, cargo)).ToUrl($"/api/planets/{planetId ?? registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    // Builds an operational shipyard, queues one CargoVessel (~2s build in the test host),
    // and polls the roster until it appears. Returns the completed ship's id.
    private async Task<Guid> BuildRosterShip(RegisterPlayerResponse registration)
    {
        var ships = await BuildRosterShips(registration, 1);
        return ships[0];
    }

    // Same as above, but queues `count` CargoVessels in parallel on one shipyard
    // (ShipyardParallelBuilds allows up to 3) and waits for all of them to land on the roster.
    private async Task<IReadOnlyList<Guid>> BuildRosterShips(RegisterPlayerResponse registration, int count)
    {
        await BuildOperationalShipyard(registration);
        for (var i = 0; i < count; i++)
        {
            await QueueShip(registration, ShipType.CargoVessel);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            var roster = await GetRoster(registration);
            if (roster.Items.Count >= count)
            {
                return [.. roster.Items.Take(count).Select(r => r.Id)];
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ships did not complete onto the roster in time.");
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
    private async Task<PlanetResponse> WaitForStock(RegisterPlayerResponse registration, decimal minOre, decimal minIngot)
    {
        var planet = await PollUntil(
            registration,
            p => p.IronOre.CurrentValue >= minOre && p.IronIngot.CurrentValue >= minIngot,
            TimeSpan.FromSeconds(30));

        Assert.True(
            planet.IronOre.CurrentValue >= minOre && planet.IronIngot.CurrentValue >= minIngot,
            $"Stock did not recover in time: ore={planet.IronOre.CurrentValue} (need {minOre}), " +
            $"ingot={planet.IronIngot.CurrentValue} (need {minIngot}).");
        return planet;
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
            s.Post.Json(new RegisterPlayerRequest($"Cargo_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
