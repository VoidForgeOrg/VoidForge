using System.Security.Claims;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Auth;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Pagination;
using Voidforge.Api.Travel;
using Wolverine;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class FleetEndpoints
{
    // Launch (#49 Move, #50 Transport, #51 Colonize). Only the Fleet stream is touched
    // (spec §2.3); the origin and destination planets are read for coordinates (and, for
    // Transport, ownership), never appended to. Arrival resolves durably via
    // CompleteFleetArrival (ADR 0001), scheduled here and handled by
    // CompleteFleetArrivalHandler — which is where Transport's cargo delivery and
    // Colonize's guarded claim happen.
    [WolverinePost("/api/fleets/{fleetId}/missions")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> Launch(
        Guid fleetId,
        LaunchMissionRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        ITravelPlanner travelPlanner,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        var requestError = ValidateLaunchRequest(request);
        if (requestError is not null)
        {
            return requestError;
        }

        var stream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = stream.Aggregate;
        if (fleet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        var playerId = principal.PlayerId();
        if (playerId != fleet.OwnerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Problem(detail: "Only a stationed fleet can be launched.", statusCode: StatusCodes.Status409Conflict);
        }

        // #60: Move/Transport to the current location is a 400 (no journey); colonize-in-place
        // is exempt — a zero-distance plan arrives at once and the guarded claim decides.
        if (request.DestinationPlanetId == fleet.LocationPlanetId && request.Mission != MissionType.Colonize)
        {
            return TypedResults.Problem(detail: "Destination must differ from the fleet's current location.", statusCode: StatusCodes.Status400BadRequest);
        }

        var destination = await session.LoadAsync<Planet>(request.DestinationPlanetId);
        if (destination is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        var missionError = ValidateMissionPrecondition(request.Mission, fleet, destination, playerId);
        if (missionError is not null)
        {
            return missionError;
        }

        // Launch touches only the Fleet stream (spec §2.3) — the origin planet is read for
        // coordinates, never appended to.
        var origin = await session.LoadAsync<Planet>(fleet.LocationPlanetId.Value)
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value;
        var speed = fleet.GetSpeed(t => balance.Ships.For(t).SpeedPerSecond);
        var plan = travelPlanner.Plan(origin.GetCoordinates(), destination.GetCoordinates(), speed, now);

        stream.AppendOne(fleet.Depart(request.DestinationPlanetId, request.Mission, plan, now));
        await bus.ScheduleAsync(new CompleteFleetArrival(fleetId, plan.ArrivesAt), plan.ArrivesAt);
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!, t => balance.Ships.For(t).CargoCapacity));
    }

    // Assembly (spec §2.3): one transaction over both streams. Ship ownership — not planet
    // ownership — is what's validated (D13): ships stranded on a foreign or unowned world
    // can still be formed into a fleet by their owner. Cargo loading (#50) is optional and,
    // when requested, additionally requires owning the planet (you cannot draw from someone
    // else's storage).
    [WolverinePost("/api/planets/{planetId}/fleets")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> Assemble(
        Guid planetId,
        AssembleFleetRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        if (request.ShipIds is null || request.ShipIds.Count == 0)
        {
            return TypedResults.Problem(detail: "shipIds must not be empty.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.ShipIds.Distinct().Count() != request.ShipIds.Count)
        {
            return TypedResults.Problem(detail: "shipIds must not contain duplicates.", statusCode: StatusCodes.Status400BadRequest);
        }

        // FetchForWriting arms Marten's optimistic-concurrency guard (#39).
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        var playerId = principal.PlayerId();
        var shipsError = ResolveOwnedShips(planet, request.ShipIds, playerId, out var ships);
        if (shipsError is not null)
        {
            return shipsError;
        }

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value;

        // Cargo additions (spec §2.3): null/both-zero skips validation (see ValidateCargo).
        var cargo = request.Cargo;
        var wantsCargo = cargo is not null && (cargo.IronOre != 0m || cargo.IronIngot != 0m);
        if (wantsCargo)
        {
            var cargoError = ValidateCargo(cargo!, ships, planet, playerId, balance, now);
            if (cargoError is not null)
            {
                return cargoError;
            }
        }

        var fleetId = Guid.NewGuid();
        stream.AppendOne(planet.RemoveShipsFromRoster(fleetId, request.ShipIds, now));

        var fleetEvents = new List<object> { Fleet.Assemble(playerId!.Value, planetId, ships, now) };
        if (wantsCargo)
        {
            AppendCargoLoad(stream, planet, fleetId, cargo!, fleetEvents, now);
        }

        session.Events.StartStream<Fleet>(fleetId, [.. fleetEvents]);
        await session.SaveChangesAsync();

        if (wantsCargo)
        {
            await RescheduleStorageFullChecks(session, bus, planetId, now);
        }

        var fleet = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(fleet!, t => balance.Ships.For(t).CargoCapacity));
    }

    // Disband (D6 counterpart): ships reach a roster only through this path. Allowed at
    // unowned/foreign planets (fleets.md); refused while cargo remains from #50 on.
    [WolverinePost("/api/fleets/{fleetId}/disband")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> Disband(
        Guid fleetId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        var fleetStream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = fleetStream.Aggregate;
        if (fleet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (principal.PlayerId() != fleet.OwnerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Problem(detail: "Only a stationed fleet can be disbanded.", statusCode: StatusCodes.Status409Conflict);
        }

        // D11 (#50): pre-validate here rather than let Fleet.Disband's own guard throw —
        // that guard is a defensive backstop (like Planet's cargo methods), not the 409 path;
        // without this check the endpoint previously surfaced an unhandled
        // InvalidOperationException as a 500 instead of the spec's 409.
        if (fleet.GetCargoLoad() > 0)
        {
            return TypedResults.Problem(detail: "Cannot disband a fleet with cargo aboard.", statusCode: StatusCodes.Status409Conflict);
        }

        var planetStream = await session.Events.FetchForWriting<Planet>(fleet.LocationPlanetId.Value);
        var planet = planetStream.Aggregate
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        var now = timeProvider.GetUtcNow();
        planetStream.AppendOne(planet.ReturnShipsToRoster(fleet.Id, fleet.ToRosterShips(), now));
        fleetStream.AppendOne(fleet.Disband(now));
        await session.SaveChangesAsync();

        var balance = balanceOptions.Value;
        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!, t => balance.Ships.For(t).CargoCapacity));
    }

    // Recall (#73, D10): turn an in-transit fleet around to head back to its origin, arriving
    // in exactly the time already elapsed. Only the Fleet stream is touched — no planet is
    // read or appended. The freshly-scheduled arrival at the return time fires the return;
    // the originally-scheduled CompleteFleetArrival(oldArrivesAt) goes stale and no-ops via
    // Fleet.Arrive's validate-on-arrival guard (ADR 0001 — no outbox cancellation).
    [WolverinePost("/api/fleets/{fleetId}/cancel")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> Cancel(
        Guid fleetId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        var stream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = stream.Aggregate;
        if (fleet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (principal.PlayerId() != fleet.OwnerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (fleet.Status != FleetStatus.InTransit)
        {
            return TypedResults.Problem(detail: "Only an in-transit fleet can be recalled.", statusCode: StatusCodes.Status409Conflict);
        }

        // Pre-check the "already returning" marker here rather than let Fleet.Recall's guard
        // throw — that guard is a defensive backstop, not the 409 path (mirrors Disband's cargo pre-check).
        if (fleet.RecalledAt is not null)
        {
            return TypedResults.Problem(detail: "Fleet is already returning.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = timeProvider.GetUtcNow();
        var recalled = fleet.Recall(now);
        stream.AppendOne(recalled);
        await bus.ScheduleAsync(
            new CompleteFleetArrival(fleetId, recalled.ReturnPlan.ArrivesAt), recalled.ReturnPlan.ArrivesAt);
        await session.SaveChangesAsync();

        var balance = balanceOptions.Value;
        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!, t => balance.Ships.For(t).CargoCapacity));
    }

    // Manual unload (spec §4/§5, #50): retry unload for a stationed fleet at a planet the
    // caller owns. Complements the automatic Transport/Colonize unload for cargo left aboard
    // after a partial delivery (destination storage was full), and for Move fleets — which
    // never auto-unload — carrying cargo to their new location.
    [WolverinePost("/api/fleets/{fleetId}/unload")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> Unload(
        Guid fleetId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        var fleetStream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = fleetStream.Aggregate;
        if (fleet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (principal.PlayerId() != fleet.OwnerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Problem(detail: "Only a stationed fleet can unload cargo.", statusCode: StatusCodes.Status409Conflict);
        }

        if (fleet.GetCargoLoad() == 0)
        {
            return TypedResults.Problem(detail: "No cargo aboard.", statusCode: StatusCodes.Status409Conflict);
        }

        var planetStream = await session.Events.FetchForWriting<Planet>(fleet.LocationPlanetId.Value);
        var planet = planetStream.Aggregate
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        if (planet.OwnerId != principal.PlayerId())
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        var now = timeProvider.GetUtcNow();
        var delivered = planet.AcceptCargoDelivery(fleet.Id, fleet.CargoIronOre, fleet.CargoIronIngot, now);
        planetStream.AppendOne(delivered);
        // Accepting 0 (destination storage full) is a legitimate outcome, not an error — the
        // fleet still ends up with whatever remains aboard, which the response reports as-is.
        fleetStream.AppendOne(fleet.UnloadCargo(fleet.LocationPlanetId.Value, delivered.IronOre, delivered.IronIngot, now));
        await session.SaveChangesAsync();

        var balance = balanceOptions.Value;
        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!, t => balance.Ships.For(t).CargoCapacity));
    }

    // The caller's fleets (mutation-adjacent view — scoped to owner rather than universe,
    // matching "my empire" reads). Disbanded fleets are history: excluded unless asked for.
    [WolverineGet("/api/fleets")]
    public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, ProblemHttpResult>> GetOwnFleets(
        ClaimsPrincipal principal,
        IQuerySession session,
        string? status = null,
        int? page = null,
        int? pageSize = null)
    {
        var parameters = PaginationParameters.Create(
            page ?? PaginationParameters.DefaultPage,
            pageSize ?? PaginationParameters.DefaultPageSize);
        if (parameters is null)
        {
            return TypedResults.Problem(detail: "page and pageSize must be >= 1.", statusCode: StatusCodes.Status400BadRequest);
        }

        // #63: `status` is a free-text query param. Omitted/empty keeps the default (exclude
        // Disbanded history); any other value must parse to a defined FleetStatus or it is a 400 —
        // never a silent empty result. Enum.TryParse alone accepts out-of-range numeric strings, so
        // guard with Enum.IsDefined too.
        FleetStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<FleetStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return TypedResults.Problem(detail: $"Unknown fleet status '{status}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            statusFilter = parsed;
        }

        var playerId = principal.PlayerId();
        var query = session.Query<Fleet>().Where(f => f.OwnerId == playerId);
        query = statusFilter is null
            ? query.Where(f => f.Status != FleetStatus.Disbanded)
            : query.Where(f => f.Status == statusFilter);

        var response = await query
            .OrderBy(f => f.AssembledAt).ThenBy(f => f.Id)
            .ToPagedResponseAsync(parameters,
                f => new FleetSummaryResponse(f.Id, f.OwnerId, f.Status, f.LocationPlanetId, f.AssembledAt, f.Ships.Count));
        return TypedResults.Ok(response);
    }

    // Universe-visible (full visibility, no fog of war in MVP).
    [WolverineGet("/api/fleets/{fleetId}")]
    public static async Task<Results<Ok<FleetResponse>, ProblemHttpResult>> GetFleet(
        Guid fleetId, IQuerySession session, IOptions<BalanceOptions> balanceOptions)
    {
        var fleet = await session.LoadAsync<Fleet>(fleetId);
        if (fleet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        var balance = balanceOptions.Value;
        return TypedResults.Ok(FleetResponse.From(fleet, t => balance.Ships.For(t).CargoCapacity));
    }

    // Universe-visible: fleets currently stationed at this planet.
    [WolverineGet("/api/planets/{planetId}/fleets")]
    public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, ProblemHttpResult>> GetPlanetFleets(
        Guid planetId,
        IQuerySession session,
        int? page = null,
        int? pageSize = null)
    {
        var planet = await session.LoadAsync<Planet>(planetId);
        if (planet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        var parameters = PaginationParameters.Create(
            page ?? PaginationParameters.DefaultPage,
            pageSize ?? PaginationParameters.DefaultPageSize);
        if (parameters is null)
        {
            return TypedResults.Problem(detail: "page and pageSize must be >= 1.", statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await session.Query<Fleet>()
            .Where(f => f.LocationPlanetId == planetId && f.Status == FleetStatus.Stationed)
            .OrderBy(f => f.AssembledAt).ThenBy(f => f.Id)
            .ToPagedResponseAsync(parameters,
                f => new FleetSummaryResponse(f.Id, f.OwnerId, f.Status, f.LocationPlanetId, f.AssembledAt, f.Ships.Count));
        return TypedResults.Ok(response);
    }

    // Mission/destination shape checks that don't need the fleet or DB yet. Returns null
    // when the request is valid; kept as its own method so Launch stays within MA0051's
    // line limit.
    private static Results<Ok<FleetResponse>, ProblemHttpResult>? ValidateLaunchRequest(
        LaunchMissionRequest request)
    {
        if (!Enum.IsDefined(request.Mission))
        {
            return TypedResults.Problem(detail: "Unknown mission type.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DestinationPlanetId == Guid.Empty)
        {
            return TypedResults.Problem(detail: "destinationPlanetId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    // Per-mission guards that need the fleet and the loaded destination (spec §2.4):
    // Transport requires a same-owner destination (re-checked on arrival — cannot fail
    // pre-combat, but the caller (playerId) equals fleet.OwnerId here); Colonize requires a
    // Colony Ship aboard. No destination-ownership check for Colonize (plan decision 1):
    // whether the destination is already owned is exactly what arrival decides (guarded
    // claim vs. ColonizationFailed), not something launch can pre-empt. Returns null when
    // the request is valid; kept as its own method so Launch stays within MA0051's line limit.
    private static Results<Ok<FleetResponse>, ProblemHttpResult>? ValidateMissionPrecondition(
        MissionType mission, Fleet fleet, Planet destination, Guid? playerId)
    {
        if (mission == MissionType.Transport && destination.OwnerId != playerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (mission == MissionType.Colonize && !fleet.Ships.Any(s => s.Type == ShipType.ColonyShip))
        {
            return TypedResults.Problem(detail: "Colonize requires a Colony Ship.", statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    // Resolves the requested ship ids against the planet's roster (409 if any are missing)
    // and checks the caller owns every one of them (403, per D13). Returns null and the
    // resolved ships via `out` when valid; kept as its own method so Assemble stays within
    // MA0051's line limit.
    private static Results<Ok<FleetResponse>, ProblemHttpResult>? ResolveOwnedShips(
        Planet planet, IReadOnlyList<Guid> shipIds, Guid? playerId, out List<RosterShip> ships)
    {
        var byId = planet.Ships.ToDictionary(s => s.Id);
        var missing = shipIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            ships = [];
            return TypedResults.Problem(detail: $"Ship(s) not on this planet's roster: {string.Join(", ", missing)}.", statusCode: StatusCodes.Status409Conflict);
        }

        ships = shipIds.Select(id => byId[id]).ToList();
        if (playerId is null || ships.Any(s => s.OwnerId != playerId))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    // Cargo checks for Assemble (spec §2.3, in order: negative → over the selected ships'
    // combined capacity → planet not owned by the caller → over what's actually stored).
    // Returns null when the request is valid; kept as its own method so Assemble stays
    // within MA0051's line limit.
    private static Results<Ok<FleetResponse>, ProblemHttpResult>? ValidateCargo(
        CargoRequest cargo,
        IReadOnlyList<RosterShip> ships,
        Planet planet,
        Guid? playerId,
        BalanceOptions balance,
        DateTimeOffset now)
    {
        if (cargo.IronOre < 0m || cargo.IronIngot < 0m)
        {
            return TypedResults.Problem(detail: "Cargo amounts cannot be negative.", statusCode: StatusCodes.Status400BadRequest);
        }

        var capacity = ships.Sum(s => balance.Ships.For(s.Type).CargoCapacity);
        if (cargo.IronOre + cargo.IronIngot > capacity)
        {
            return TypedResults.Problem(detail: "Cargo exceeds the selected ships' combined capacity.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (planet.OwnerId != playerId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        if (cargo.IronOre > planet.IronOre.GetCurrentValue(now) || cargo.IronIngot > planet.IronIngot.GetCurrentValue(now))
        {
            return TypedResults.Problem(detail: "Insufficient stored resources for the requested cargo.", statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    // Loads the requested cargo off the planet's storage onto the new fleet (spec §2.3) and
    // adds the Fleet-side CargoLoaded to fleetEvents. Per D6 (#69), the freed output storage may
    // let a producer halted OutputStorageFull resume: EvaluateStorageResumesAfterLoad reads the
    // POST-load pool values WITHOUT mutating `planet` (which must stay pristine until commit —
    // UseIdentityMapForAggregates re-applies the appended load onto this very instance at
    // SaveChanges), and any BuildingResumed is appended to the planet stream so its RebaseRates
    // restores the producer's rate atomically with the load. Kept out of Assemble for MA0051.
    private static void AppendCargoLoad(
        IEventStream<Planet> stream, Planet planet, Guid fleetId, CargoRequest cargo,
        List<object> fleetEvents, DateTimeOffset now)
    {
        stream.AppendOne(planet.LoadCargoFromStorage(fleetId, cargo.IronOre, cargo.IronIngot, now));
        fleetEvents.Add(new CargoLoaded(cargo.IronOre, cargo.IronIngot, now));

        var resumes = planet.EvaluateStorageResumesAfterLoad(cargo.IronOre, cargo.IronIngot, now);
        if (resumes.Count > 0)
        {
            stream.AppendMany([.. resumes]);
        }
    }

    // After a cargo load commits (#69/#70): the load lowered the pool and may have resumed a producer
    // (new positive rate), so reschedule all cascade checks from the post-commit state — a resumed
    // producer must re-halt when it refills, and freeing/draining a pool shifts the depletion and
    // buffer-empty deadlines too. Mirrors the other rate-changing commit sites.
    private static async Task RescheduleStorageFullChecks(
        IDocumentSession session, IMessageBus bus, Guid planetId, DateTimeOffset now)
    {
        var updatedPlanet = await session.Events.FetchLatest<Planet>(planetId);
        await StorageHaltScheduling.ScheduleAllChecksAsync(bus, planetId, updatedPlanet!, now);
    }
}
