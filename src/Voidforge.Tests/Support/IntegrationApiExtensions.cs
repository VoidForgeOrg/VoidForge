using Alba;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Support;

/// <summary>
/// Shared API-driving helpers for the integration suite (#62). All helpers assert
/// success (200) unless the name says otherwise; polling helpers return the last
/// state on timeout so the caller's assertion reports the failure.
/// </summary>
public static class IntegrationApiExtensions
{
    /// <summary>
    /// Runs a scenario WITHOUT asserting status, then enforces the universal "no 5xx" guarantee:
    /// a 5xx (except the modeled 503) throws <see cref="ServerErrorException"/> — a caller must
    /// never receive a 500. Returns the raw result so the caller can assert the expected status
    /// (via <see cref="EnsureExpected"/>) and read the body. This is the single choke point every
    /// asserting helper flows through.
    /// </summary>
    public static async Task<IScenarioResult> Send(this IAlbaHost host, Action<Scenario> configure)
    {
        var result = await host.Scenario(s =>
        {
            configure(s);
            s.IgnoreStatusCode();
        });

        var status = result.Context.Response.StatusCode;
        if (status is >= 500 and not 503)
        {
            var body = await result.ReadAsTextAsync();
            throw new ServerErrorException(
                status, result.Context.Request.Method, result.Context.Request.Path.ToString(), body);
        }

        return result;
    }

    /// <summary>
    /// Enforces an expected status for the modeled path (5xx is already handled by <see cref="Send"/>).
    /// A mismatch throws <see cref="UnexpectedStatusException"/>, which contention-tolerant callers MAY catch.
    /// </summary>
    public static void EnsureExpected(this IScenarioResult result, int expected)
    {
        var status = result.Context.Response.StatusCode;
        if (status != expected)
        {
            throw new UnexpectedStatusException(
                expected, status, result.Context.Request.Method, result.Context.Request.Path.ToString());
        }
    }

    public static async Task<RegisterPlayerResponse> RegisterPlayer(this IAlbaHost host, string namePrefix)
    {
        var result = await host.Send(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"{namePrefix}{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
        });
        result.EnsureExpected(200);

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }

    public static async Task<T> GetJson<T>(this IAlbaHost host, RegisterPlayerResponse asWhom, string url)
    {
        var result = await host.Send(s =>
        {
            s.Get.Url(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, asWhom.ApiKey);
        });
        result.EnsureExpected(200);

        var response = await result.ReadAsJsonAsync<T>();
        Assert.NotNull(response);
        return response;
    }

    public static Task<PlanetResponse> GetPlanet(this IAlbaHost host, RegisterPlayerResponse registration)
        => host.GetPlanetById(registration, registration.HomeworldId);

    /// <summary>GETs <c>/api/players/me</c> and returns the lazily-computed player score (#67).</summary>
    public static async Task<decimal> GetScore(this IAlbaHost host, RegisterPlayerResponse asWhom)
    {
        var me = await host.GetJson<PlayerInfoResponse>(asWhom, "/api/players/me");
        return me.Score;
    }

    public static Task<PlanetResponse> GetPlanetById(
        this IAlbaHost host, RegisterPlayerResponse asWhom, Guid planetId)
        => host.GetJson<PlanetResponse>(asWhom, $"/api/planets/{planetId}");

    public static Task<PagedResponse<RosterShipResponse>> GetRoster(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid? planetId = null)
        => host.GetJson<PagedResponse<RosterShipResponse>>(
            registration, $"/api/planets/{planetId ?? registration.HomeworldId}/ships?pageSize=200");

    public static async Task<ShipBuildResponse> QueueShip(
        this IAlbaHost host, RegisterPlayerResponse registration, ShipType type)
    {
        var result = await host.Send(s =>
        {
            s.Post.Json(new QueueShipRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var build = await result.ReadAsJsonAsync<ShipBuildResponse>();
        Assert.NotNull(build);
        return build;
    }

    /// <summary>Places a Shipyard only if the planet has none, then polls until it is Operational.</summary>
    public static async Task EnsureOperationalShipyard(this IAlbaHost host, RegisterPlayerResponse registration)
    {
        var planet = await host.GetPlanet(registration);
        if (!planet.Buildings.Any(b => b.Type == BuildingType.Shipyard))
        {
            var result = await host.Send(s =>
            {
                s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                    .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
                s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            });
            result.EnsureExpected(200);
        }

        await host.PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == BuildingType.Shipyard && b.Status == BuildingStatus.Operational),
            TestTimeouts.Completion);
    }

    /// <summary>
    /// Ensures an operational shipyard, queues one ship, and returns the id of the ship
    /// that newly appears on the roster (diff-based, so pre-existing roster ships are fine).
    /// </summary>
    public static async Task<Guid> BuildRosterShip(
        this IAlbaHost host, RegisterPlayerResponse registration, ShipType type = ShipType.CargoVessel)
    {
        await host.EnsureOperationalShipyard(registration);

        var before = await host.GetRoster(registration);
        var known = before.Items.Select(s => s.Id).ToHashSet();
        await host.QueueShip(registration, type);

        var deadline = DateTime.UtcNow + TestTimeouts.Completion;
        do
        {
            var roster = await host.GetRoster(registration);
            var added = roster.Items.FirstOrDefault(s => !known.Contains(s.Id));
            if (added is not null)
            {
                return added.Id;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
    }

    /// <summary>Queues <paramref name="count"/> CargoVessels and waits for all of them to reach the roster.</summary>
    public static async Task<IReadOnlyList<Guid>> BuildRosterShips(
        this IAlbaHost host, RegisterPlayerResponse registration, int count)
    {
        await host.EnsureOperationalShipyard(registration);

        var before = await host.GetRoster(registration);
        var known = before.Items.Select(s => s.Id).ToHashSet();
        for (var i = 0; i < count; i++)
        {
            await host.QueueShip(registration, ShipType.CargoVessel);
        }

        var deadline = DateTime.UtcNow + TestTimeouts.StockRecovery;
        do
        {
            var roster = await host.GetRoster(registration);
            var added = roster.Items.Where(s => !known.Contains(s.Id)).Select(s => s.Id).ToList();
            if (added.Count >= count)
            {
                return added;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException($"Queued {count} ships did not all reach the roster in time.");
    }

    public static async Task<FleetResponse> AssembleFleet(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        IReadOnlyList<Guid> shipIds,
        CargoRequest? cargo = null,
        Guid? planetId = null)
    {
        var result = await host.Send(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds, cargo))
                .ToUrl($"/api/planets/{planetId ?? registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task<FleetResponse> Launch(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        MissionType mission,
        Guid destinationPlanetId)
    {
        var result = await host.Send(s =>
        {
            s.Post.Json(new LaunchMissionRequest(mission, destinationPlanetId))
                .ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task<FleetResponse> Disband(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await host.Send(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task<FleetResponse> Recall(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await host.Send(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/cancel");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    /// <summary>POSTs the bodyless cancel and returns the raw status code — for 409/403 cases.</summary>
    public static async Task<int> CancelForStatus(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/cancel");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
    }

    public static async Task<FleetResponse> Unload(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await host.Send(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/unload");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    /// <summary>Polls the planet until ore and ingot stocks reach the minimums; asserts on timeout.</summary>
    public static async Task<PlanetResponse> WaitForStock(
        this IAlbaHost host, RegisterPlayerResponse registration, decimal minOre, decimal minIngot)
    {
        var planet = await host.PollUntil(
            registration,
            p => p.IronOre.CurrentValue >= minOre && p.IronIngot.CurrentValue >= minIngot,
            TestTimeouts.StockRecovery);

        Assert.True(
            planet.IronOre.CurrentValue >= minOre && planet.IronIngot.CurrentValue >= minIngot,
            $"Stock did not recover in time: ore={planet.IronOre.CurrentValue} (need {minOre}), " +
            $"ingot={planet.IronIngot.CurrentValue} (need {minIngot}).");

        return planet;
    }

    /// <summary>
    /// Polls the planet (homeworld unless <paramref name="planetId"/> is given) until the
    /// predicate holds. Returns the last-seen state on timeout — callers assert and report.
    /// Test wall-clock timeout — unrelated to the app's injected TimeProvider.
    /// </summary>
    public static async Task<PlanetResponse> PollUntil(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Func<PlanetResponse, bool> predicate,
        TimeSpan timeout,
        Guid? planetId = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        PlanetResponse planet;
        do
        {
            planet = await host.GetPlanetById(registration, planetId ?? registration.HomeworldId);
            if (predicate(planet))
            {
                return planet;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        return planet;
    }

    public static async Task<FleetResponse> PollFleetUntil(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        Func<FleetResponse, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        FleetResponse fleet;
        do
        {
            fleet = await host.GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
            if (predicate(fleet))
            {
                return fleet;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        return fleet;
    }

    // Bounded ConcurrencyException retry for handler-invoked arrivals. A direct
    // CompleteFleetArrivalHandler.Handle call bypasses Wolverine, so the #39 durable-message retry
    // ladder (Program.cs) never sees a collision with the real scheduler's delivery of the same
    // CompleteFleetArrival — this stands in for it. See CompleteArrivalWithRetry.
    private const int _arrivalMaxAttempts = 5;
    private const int _arrivalRetryDelayMs = 100;

    /// <summary>
    /// Launches the mission, then completes the arrival immediately by invoking the
    /// handler directly with the scheduled ArrivesAt — no wall-clock wait.
    /// </summary>
    public static async Task<FleetResponse> LaunchAndArriveInstantly(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        MissionType mission,
        Guid destinationPlanetId)
    {
        var launched = await host.Launch(registration, fleetId, mission, destinationPlanetId);
        Assert.NotNull(launched.ArrivesAt);

        await host.CompleteArrivalWithRetry(fleetId, launched.ArrivesAt.Value);

        return await host.GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
    }

    /// <summary>
    /// Invokes CompleteFleetArrivalHandler directly, tolerant of a concurrent durable-scheduler
    /// delivery of the same CompleteFleetArrival. AppFixture boots the real Wolverine Solo scheduler
    /// with fast ship speeds, so a scheduled arrival can land on the same Fleet stream at the same
    /// instant as this manual call; the loser's SaveChangesAsync throws
    /// EventStreamUnexpectedMaxEventIdException (a ConcurrencyException). A direct call never reaches
    /// the #39 retry ladder, so retry here with a fresh session per attempt — on retry the fleet is
    /// already Stationed, so Fleet.Arrive no-ops and the call converges.
    /// </summary>
    public static async Task CompleteArrivalWithRetry(
        this IAlbaHost host, Guid fleetId, DateTimeOffset arrivesAt)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        for (var attempt = 1; attempt <= _arrivalMaxAttempts; attempt++)
        {
            await using var session = store.LightweightSession();
            try
            {
                await CompleteFleetArrivalHandler.Handle(
                    new CompleteFleetArrival(fleetId, arrivesAt), session);
                return;
            }
            catch (ConcurrencyException) when (attempt < _arrivalMaxAttempts)
            {
                await Task.Delay(_arrivalRetryDelayMs);
            }
        }
    }

    /// <summary>POSTs and returns the raw status code — for race tests that expect non-200s.</summary>
    public static async Task<int> PostForStatus(
        this IAlbaHost host, RegisterPlayerResponse registration, string url, object payload)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(payload).ToUrl(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
    }

    /// <summary>First planet in the universe that is not the caller's homeworld.</summary>
    public static async Task<Guid> FindPlanetOtherThan(this IAlbaHost host, RegisterPlayerResponse registration)
    {
        var systems = await host.GetJson<PagedResponse<SolarSystemResponse>>(
            registration, "/api/solar-systems?pageSize=200");
        var planetId = systems.Items
            .SelectMany(sys => sys.PlanetIds)
            .First(id => id != registration.HomeworldId);
        return planetId;
    }

    /// <summary>
    /// Scans the public API for an unowned planet, optionally excluding one solar system.
    /// Throws if the universe has none.
    /// </summary>
    public static async Task<Guid> FindUncolonizedPlanet(
        this IAlbaHost host, RegisterPlayerResponse asWhom, Guid? excludeSystemId = null)
    {
        var systems = await host.GetJson<PagedResponse<SolarSystemResponse>>(
            asWhom, "/api/solar-systems?pageSize=200");
        foreach (var system in systems.Items)
        {
            if (system.Id == excludeSystemId)
            {
                continue;
            }

            foreach (var planetId in system.PlanetIds)
            {
                var planet = await host.GetPlanetById(asWhom, planetId);
                if (planet.OwnerId is null)
                {
                    return planetId;
                }
            }
        }

        throw new InvalidOperationException("No uncolonized planet found in the universe.");
    }

    /// <summary>
    /// Places a building via <c>POST /api/planets/{id}/buildings</c>, asserts 200, and returns the
    /// post-place planet. The just-placed slot is the LAST entry in <see cref="PlanetResponse.Buildings"/>
    /// — the Buildings list is append-only, so <c>Buildings.Count - 1</c> is its stable SlotIndex.
    /// </summary>
    public static async Task<PlanetResponse> PlaceBuilding(
        this IAlbaHost host, RegisterPlayerResponse registration, BuildingType type, Guid? planetId = null)
    {
        var result = await host.Send(s =>
        {
            s.Post.Json(new PlaceBuildingRequest(type))
                .ToUrl($"/api/planets/{planetId ?? registration.HomeworldId}/buildings");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(200);

        var planet = await result.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(planet);
        return planet;
    }

    /// <summary>
    /// Demolishes a completed building via <c>POST /api/planets/{id}/buildings/{slot}/demolish</c> and
    /// asserts the 202 Accepted the endpoint returns (#72); the slot flips to Demolishing immediately
    /// (leaving the Operational set) and a durable completion tombstones it.
    /// </summary>
    public static async Task DemolishBuilding(
        this IAlbaHost host, RegisterPlayerResponse registration, int slotIndex, Guid? planetId = null)
    {
        var result = await host.Send(s =>
        {
            s.Post.Url($"/api/planets/{planetId ?? registration.HomeworldId}/buildings/{slotIndex}/demolish");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(202);
    }

    /// <summary>
    /// Cancels an in-progress construction via <c>DELETE /api/planets/{id}/buildings/{slot}/construction</c>
    /// and asserts the 204 No Content the endpoint returns (#72); the slot becomes a Cancelled tombstone.
    /// </summary>
    public static async Task CancelConstruction(
        this IAlbaHost host, RegisterPlayerResponse registration, int slotIndex, Guid? planetId = null)
    {
        var result = await host.Send(s =>
        {
            s.Delete.Url($"/api/planets/{planetId ?? registration.HomeworldId}/buildings/{slotIndex}/construction");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        });
        result.EnsureExpected(204);
    }

    /// <summary>
    /// Polls the planet until a building of <paramref name="type"/> is <see cref="BuildingStatus.Halted"/>
    /// with <paramref name="reason"/>, then returns that slot. PollUntil returns the last-seen state on
    /// timeout, so the trailing NotNull surfaces a failure to halt as the assertion.
    /// </summary>
    public static async Task<BuildingSlotResponse> PollBuildingUntilHalted(
        this IAlbaHost host, RegisterPlayerResponse registration, BuildingType type, HaltReason reason)
    {
        var planet = await host.PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == type && b.Status == BuildingStatus.Halted && b.HaltReason == reason),
            TestTimeouts.Completion);

        var slot = planet.Buildings.FirstOrDefault(
            b => b.Type == type && b.Status == BuildingStatus.Halted && b.HaltReason == reason);
        Assert.NotNull(slot);
        return slot;
    }

    /// <summary>
    /// Polls the planet until a building of <paramref name="type"/> is back to
    /// <see cref="BuildingStatus.Operational"/> (the resume assertion, #69), then returns that slot.
    /// PollUntil returns the last-seen state on timeout, so the trailing NotNull surfaces a failure.
    /// </summary>
    public static async Task<BuildingSlotResponse> PollBuildingUntilOperational(
        this IAlbaHost host, RegisterPlayerResponse registration, BuildingType type)
    {
        var planet = await host.PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == type && b.Status == BuildingStatus.Operational),
            TestTimeouts.Completion);

        var slot = planet.Buildings.FirstOrDefault(b => b.Type == type && b.Status == BuildingStatus.Operational);
        Assert.NotNull(slot);
        return slot;
    }
}
