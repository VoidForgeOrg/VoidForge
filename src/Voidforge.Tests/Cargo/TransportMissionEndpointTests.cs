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

// Task 5 (#50, spec §2.4): Transport mission launch guards + the codebase's first
// cross-aggregate arrival append (Planet storage credited, Fleet cargo zeroed, one commit).
[Collection(IntegrationCollection.Name)]
public sealed class TransportMissionEndpointTests
{
    private readonly IAlbaHost _host;

    public TransportMissionEndpointTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchTransportToForeignDestinationReturns403()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Transport, foreign.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task LaunchTransportToOwnDestinationReturns200AndTransitionsToInTransit()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        var fleet = await AssembleFleet(owner, [shipId]);
        // #51 makes a second owned planet reachable via the API (colonization) — arranged
        // directly here since Transport's same-owner destination requirement needs one now.
        var destinationId = await ColonizeSecondPlanetForOwner(owner);

        var launched = await Launch(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(destinationId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Transport, launched.Mission);
    }

    [Fact]
    public async Task HandlerInvokedTransportArrivalCreditsDestinationAndZeroesFleetCargo()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // empty storage: full headroom

        var arrived = await LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.Equal(0m, arrived.CargoIronOre);
        Assert.Equal(0m, arrived.CargoIronIngot);

        var destination = await GetPlanetById(owner, destinationId);
        Assert.Equal(100m, destination.IronOre.CurrentValue);
        Assert.Equal(50m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task DuplicateTransportArrivalIsANoOpAndDoesNotDoubleDeliverCargo()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // empty storage: full headroom

        var launched = await Launch(owner, fleet.Id, MissionType.Transport, destinationId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using (var firstSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), firstSession);
        }

        var afterFirst = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(0m, afterFirst.CargoIronOre);
        Assert.Equal(0m, afterFirst.CargoIronIngot);
        var destinationAfterFirst = await GetPlanetById(owner, destinationId);
        Assert.Equal(100m, destinationAfterFirst.IronOre.CurrentValue);
        Assert.Equal(50m, destinationAfterFirst.IronIngot.CurrentValue);

        // Redelivery of the identical message (Wolverine's at-least-once delivery, ADR 0001):
        // the fleet is no longer InTransit, so Arrive() returns no events and the handler
        // returns before ever touching the Planet stream — must not double-credit storage.
        await using (var secondSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), secondSession);
        }

        var afterDuplicate = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(destinationId, afterDuplicate.LocationPlanetId);
        Assert.Equal(0m, afterDuplicate.CargoIronOre);
        Assert.Equal(0m, afterDuplicate.CargoIronIngot);

        var destinationAfterDuplicate = await GetPlanetById(owner, destinationId);
        Assert.Equal(destinationAfterFirst.IronOre.CurrentValue, destinationAfterDuplicate.IronOre.CurrentValue);
        Assert.Equal(destinationAfterFirst.IronIngot.CurrentValue, destinationAfterDuplicate.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedTransportArrivalWithFullDestinationStorageLeavesCargoAboard()
    {
        var owner = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(100m, 50m));
        // Colonize the destination already at its storage cap (WorldGenOptions defaults:
        // 10000 ore / 5000 ingot) so AcceptCargoDelivery has zero headroom for either pool.
        var destinationId = await ColonizeSecondPlanetForOwner(owner, ironOreStored: 10_000, ironIngotStored: 5_000);

        var arrived = await LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Transport, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        // Nothing fit: cargo stays aboard rather than being lost.
        Assert.Equal(100m, arrived.CargoIronOre);
        Assert.Equal(50m, arrived.CargoIronIngot);

        var destination = await GetPlanetById(owner, destinationId);
        Assert.Equal(10_000m, destination.IronOre.CurrentValue);
        Assert.Equal(5_000m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedMoveArrivalWithCargoLeavesCargoUntouched()
    {
        var owner = await RegisterPlayer();
        var beta = await RegisterPlayer();   // another colonized planet to Move to
        var shipId = await BuildRosterShip(owner);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [shipId], new CargoRequest(80m, 20m));

        var arrived = await LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(beta.HomeworldId, arrived.LocationPlanetId);
        // Move never auto-delivers cargo (spec §2.4) — only Transport/Colonize do.
        Assert.Equal(80m, arrived.CargoIronOre);
        Assert.Equal(20m, arrived.CargoIronIngot);
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

    // Launches the mission, then resolves arrival via the handler-invoked pattern (spec §7
    // testing strategy item 2), bypassing the real scheduled envelope.
    private async Task<FleetResponse> LaunchAndArriveInstantly(
        RegisterPlayerResponse registration, Guid fleetId, MissionType mission, Guid destinationPlanetId)
    {
        var launched = await Launch(registration, fleetId, mission, destinationPlanetId);
        Assert.NotNull(launched.ArrivesAt);

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleetId, launched.ArrivesAt.Value), session);

        return await GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
    }

    // Test arrangement, not production code: #51 (colonization) is the API path that will
    // make a second same-owner planet reachable; until then, appending PlanetColonized
    // directly to an uncolonized planet's stream is how Transport's happy paths get a
    // same-owner destination to target. Mirrors PlayerEndpoints.Register's colonization.
    private async Task<Guid> ColonizeSecondPlanetForOwner(
        RegisterPlayerResponse owner, long ironOreStored = 0, long ironIngotStored = 0)
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();
        var planetId = uncolonized[0];

        // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
        session.Events.Append(planetId, new PlanetColonized(owner.PlayerId, ironOreStored, ironIngotStored, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        return planetId;
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

    // Builds an operational shipyard, queues one CargoVessel (~2s build in the test host),
    // and polls the roster until it appears. Returns the completed ship's id.
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
        => await GetPlanetById(registration, registration.HomeworldId);

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
            s.Post.Json(new RegisterPlayerRequest($"Transport_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
