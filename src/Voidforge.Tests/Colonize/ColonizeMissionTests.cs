using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Colonize;

// #51 (spec §2.4): Colonize mission launch guard (Colony Ship required) + the
// guarded arrival claim (planet owned by the fleet owner, colony ship consumed, cargo
// delivered) vs. the lost-the-race/already-owned branch (fleet idles Stationed, nothing
// aboard changes).
[Collection(IntegrationCollection.Name)]
public sealed class ColonizeMissionTests
{
    private readonly IAlbaHost _host;

    public ColonizeMissionTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchColonizeWithoutAColonyShipAboardReturns409()
    {
        var owner = await RegisterPlayer();
        var foreign = await RegisterPlayer();
        var shipId = await BuildRosterShip(owner, ShipType.CargoVessel);
        var fleet = await AssembleFleet(owner, [shipId]);

        await _host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(MissionType.Colonize, foreign.HomeworldId))
                .ToUrl($"/api/fleets/{fleet.Id}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, owner.ApiKey);
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task LaunchColonizeWithAColonyShipAboardReturns200AndTransitionsToInTransit()
    {
        var owner = await RegisterPlayer();
        var colonyShipId = await BuildRosterShip(owner, ShipType.ColonyShip);
        var fleet = await AssembleFleet(owner, [colonyShipId]);
        var destinationId = await UncolonizedPlanetId();

        var launched = await Launch(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Equal(destinationId, launched.DestinationPlanetId);
        Assert.Equal(MissionType.Colonize, launched.Mission);
    }

    [Fact]
    public async Task HandlerInvokedColonizeArrivalAtAnUncolonizedPlanetClaimsItConsumesTheColonyShipAndDeliversCargo()
    {
        var owner = await RegisterPlayer();
        var colonyShipId = await BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await BuildRosterShip(owner, ShipType.CargoVessel);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await UncolonizedPlanetId();

        var arrived = await LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.DoesNotContain(arrived.Ships, s => s.Id == colonyShipId);   // consumed on claim
        Assert.Contains(arrived.Ships, s => s.Id == cargoVesselId);        // untouched
        Assert.Equal(0m, arrived.CargoIronOre);
        Assert.Equal(0m, arrived.CargoIronIngot);

        var destination = await GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destination.OwnerId);
        Assert.Equal(100m, destination.IronOre.CurrentValue);
        Assert.Equal(50m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task HandlerInvokedColonizeArrivalAtAnAlreadyOwnedPlanetLeavesOwnerShipAndCargoIntactAndFleetStationed()
    {
        var owner = await RegisterPlayer();
        var colonyShipId = await BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await BuildRosterShip(owner, ShipType.CargoVessel);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await ColonizeSecondPlanetForOwner(owner);   // already owned before arrival

        var arrived = await LaunchAndArriveInstantly(owner, fleet.Id, MissionType.Colonize, destinationId);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.Contains(arrived.Ships, s => s.Id == colonyShipId);    // ship preserved: lost the race
        Assert.Contains(arrived.Ships, s => s.Id == cargoVesselId);
        Assert.Equal(100m, arrived.CargoIronOre);                     // cargo intact: never delivered
        Assert.Equal(50m, arrived.CargoIronIngot);

        var destination = await GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destination.OwnerId);   // owner unchanged
        Assert.Equal(0m, destination.IronOre.CurrentValue);
        Assert.Equal(0m, destination.IronIngot.CurrentValue);
    }

    [Fact]
    public async Task DuplicateColonizeArrivalIsANoOpAndDoesNotDoubleClaimOrDoubleDeliverCargo()
    {
        var owner = await RegisterPlayer();
        var colonyShipId = await BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await BuildRosterShip(owner, ShipType.CargoVessel);
        await WaitForStock(owner, 150m, 100m);
        var fleet = await AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));
        var destinationId = await UncolonizedPlanetId();

        var launched = await Launch(owner, fleet.Id, MissionType.Colonize, destinationId);
        Assert.NotNull(launched.ArrivesAt);
        var arrivesAt = launched.ArrivesAt.Value;

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using (var firstSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), firstSession);
        }

        var afterFirst = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.DoesNotContain(afterFirst.Ships, s => s.Id == colonyShipId);
        Assert.Equal(0m, afterFirst.CargoIronOre);
        Assert.Equal(0m, afterFirst.CargoIronIngot);
        var destinationAfterFirst = await GetPlanetById(owner, destinationId);
        Assert.Equal(owner.PlayerId, destinationAfterFirst.OwnerId);
        Assert.Equal(100m, destinationAfterFirst.IronOre.CurrentValue);
        Assert.Equal(50m, destinationAfterFirst.IronIngot.CurrentValue);

        // Redelivery of the identical message (Wolverine's at-least-once delivery, ADR 0001):
        // the fleet is no longer InTransit, so Arrive() returns no events and the handler
        // returns before ever touching the Planet stream — must not double-claim or
        // double-deliver cargo.
        await using (var secondSession = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, arrivesAt), secondSession);
        }

        var afterDuplicate = await GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterDuplicate.Status);
        Assert.Equal(destinationId, afterDuplicate.LocationPlanetId);
        Assert.Equal(afterFirst.Ships.Select(s => s.Id).OrderBy(id => id), afterDuplicate.Ships.Select(s => s.Id).OrderBy(id => id));
        Assert.Equal(0m, afterDuplicate.CargoIronOre);
        Assert.Equal(0m, afterDuplicate.CargoIronIngot);

        var destinationAfterDuplicate = await GetPlanetById(owner, destinationId);
        Assert.Equal(destinationAfterFirst.OwnerId, destinationAfterDuplicate.OwnerId);
        Assert.Equal(destinationAfterFirst.IronOre.CurrentValue, destinationAfterDuplicate.IronOre.CurrentValue);
        Assert.Equal(destinationAfterFirst.IronIngot.CurrentValue, destinationAfterDuplicate.IronIngot.CurrentValue);
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

    // Raw query for a planet nobody owns yet — the Colonize mission's natural destination.
    // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
    private async Task<Guid> UncolonizedPlanetId()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();

        return uncolonized[0];
    }

    // Test arrangement, not production code: appends PlanetColonized directly to an
    // uncolonized planet's stream so a Colonize arrival can be arranged against an
    // ALREADY-owned destination without depending on a second player's registration.
    // Mirrors TransportMissionEndpointTests.ColonizeSecondPlanetForOwner.
    // Relies on the IntegrationCollection's serialized test arrangement (no concurrent writer); must not be copied into a parallel context.
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

    // Builds an operational shipyard (once per homeworld — reused across calls) and queues
    // one ship of the requested type, polling the roster until a ship absent from the
    // pre-queue roster snapshot appears. Extends TransportMissionEndpointTests'
    // CargoVessel-only helper with a ShipType parameter and before/after roster diffing so
    // a homeworld can accumulate both a Colony Ship and a Cargo Vessel across two calls.
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
            s.Post.Json(new RegisterPlayerRequest($"Colonize_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
