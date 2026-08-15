using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Cargo;

// #50 (spec §2.3/§4/§5): cargo loaded at assembly + the manual unload endpoint.
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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        // Shipyard/ship construction drains ingots hard (test-host drain rates exceed the
        // homeworld's +10/s production) — wait for it to recover past what we're about to
        // load, plus margin, before taking the "before" snapshot.
        var before = await _host.WaitForStock(owner, 150m, 100m);

        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));

        Assert.Equal(100m, fleet.CargoIronOre);
        Assert.Equal(50m, fleet.CargoIronIngot);
        Assert.Equal(500m, fleet.CargoCapacity);   // one CargoVessel, spec §6 default

        var after = await _host.GetPlanet(owner);
        // Production continues (Drill/Refinery) between reads, so allow slack either side
        // of the exact 100/50 the load subtracted.
        Assert.InRange(before.IronOre.CurrentValue - after.IronOre.CurrentValue, 90m, 130m);
        Assert.InRange(before.IronIngot.CurrentValue - after.IronIngot.CurrentValue, 30m, 80m);
    }

    [Fact]
    public async Task AssembleWithNoCargoLeavesFleetEmptyAndSkipsValidation()
    {
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);

        var fleet = await _host.AssembleFleet(owner, [shipId]);

        Assert.Equal(0m, fleet.CargoIronOre);
        Assert.Equal(0m, fleet.CargoIronIngot);
    }

    [Fact]
    public async Task AssembleCargoExceedingCapacityReturns400()
    {
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);   // one CargoVessel: capacity 500

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var foreign = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);   // no cargo yet

        // Move (cargo-free) to the foreign homeworld, resolved instantly via the
        // handler-invoked pattern (avoids waiting on the real scheduled arrival).
        await MoveAndArriveInstantly(owner, fleet.Id, foreign.HomeworldId);
        await _host.Disband(owner, fleet.Id);   // ships land on the foreign planet's roster (D13: still owner-owned)

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

    // D13 regression (carry-over from #50's review): assembling WITHOUT cargo only
    // validates ship ownership, never planet ownership — the inverse of
    // AssembleCargoOnForeignPlanetReturns403EvenThoughShipsAreOwned above, which requests
    // cargo and is correctly refused. Ships owned by the caller but stranded on a foreign
    // planet's roster must still be re-assemblable there when no cargo is requested.
    [Fact]
    public async Task AssembleWithoutCargoOnForeignPlanetReturns200()
    {
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var foreign = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);   // no cargo yet

        // Move (cargo-free) to the foreign homeworld, resolved instantly via the
        // handler-invoked pattern (avoids waiting on the real scheduled arrival).
        await MoveAndArriveInstantly(owner, fleet.Id, foreign.HomeworldId);
        await _host.Disband(owner, fleet.Id);   // ships land on the foreign planet's roster (D13: still owner-owned)

        // The roster is on the foreign planet now, not owner's own homeworld.
        var reassembled = await _host.AssembleFleet(owner, [shipId], planetId: foreign.HomeworldId);

        Assert.Equal(0m, reassembled.CargoIronOre);
        Assert.Equal(0m, reassembled.CargoIronIngot);
        Assert.Equal(foreign.HomeworldId, reassembled.LocationPlanetId);
    }

    [Fact]
    public async Task AssembleCargoExceedingStoredAmountReturns409()
    {
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipIds = await _host.BuildRosterShips(owner, 2);   // two CargoVessels: capacity 1000

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);   // no cargo

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var foreign = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(50m, 0m));

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
        var owner = await _host.RegisterPlayer("Cargo_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var beforeAssemble = await _host.WaitForStock(owner, 150m, 100m);

        var fleet = await _host.AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var afterAssemble = await _host.GetPlanet(owner);
        Assert.True(afterAssemble.IronOre.CurrentValue < beforeAssemble.IronOre.CurrentValue);

        var unloaded = await _host.Unload(owner, fleet.Id);
        Assert.Equal(0m, unloaded.CargoIronOre);
        Assert.Equal(0m, unloaded.CargoIronIngot);

        var afterUnload = await _host.GetPlanet(owner);
        // The 100/50 that left comes back (net of whatever accrued meanwhile).
        Assert.InRange(afterUnload.IronOre.CurrentValue - afterAssemble.IronOre.CurrentValue, 90m, 130m);
        Assert.InRange(afterUnload.IronIngot.CurrentValue - afterAssemble.IronIngot.CurrentValue, 30m, 80m);
    }

    // Handler-invoked resolution (spec §7 testing strategy item 2) — verifies the
    // handler → stream append path without waiting on the real scheduled envelope.
    private Task<FleetResponse> MoveAndArriveInstantly(
        RegisterPlayerResponse registration, Guid fleetId, Guid destinationPlanetId)
        => _host.LaunchAndArriveInstantly(registration, fleetId, MissionType.Move, destinationPlanetId);
}
