using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
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
    // Launch (#49 Move, #50 Transport — Colonize dispatch lands in #51). Only the Fleet
    // stream is touched (spec §2.3); the origin and destination planets are read for
    // coordinates (and, for Transport, ownership), never appended to. Arrival resolves
    // durably via CompleteFleetArrival (ADR 0001), scheduled here and handled by
    // CompleteFleetArrivalHandler — which is where Transport's cargo delivery happens.
    [WolverinePost("/api/fleets/{fleetId}/missions")]
    public static async Task<Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>> Launch(
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
            return TypedResults.NotFound();
        }

        var playerId = PlayerId(principal);
        if (playerId != fleet.OwnerId)
        {
            return TypedResults.Forbid();
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Conflict("Only a stationed fleet can be launched.");
        }

        if (request.DestinationPlanetId == fleet.LocationPlanetId)
        {
            return TypedResults.BadRequest("Destination must differ from the fleet's current location.");
        }

        var destination = await session.LoadAsync<Planet>(request.DestinationPlanetId);
        if (destination is null)
        {
            return TypedResults.NotFound();
        }

        // Transport requires a same-owner destination (spec §2.4); re-checked on arrival —
        // cannot fail pre-combat, but the caller (playerId) equals fleet.OwnerId here.
        if (request.Mission == MissionType.Transport && destination.OwnerId != playerId)
        {
            return TypedResults.Forbid();
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
    public static async Task<Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>> Assemble(
        Guid planetId,
        AssembleFleetRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        if (request.ShipIds is null || request.ShipIds.Count == 0)
        {
            return TypedResults.BadRequest("shipIds must not be empty.");
        }

        if (request.ShipIds.Distinct().Count() != request.ShipIds.Count)
        {
            return TypedResults.BadRequest("shipIds must not contain duplicates.");
        }

        // FetchForWriting arms Marten's optimistic-concurrency guard (#39).
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        var playerId = PlayerId(principal);
        var shipsError = ResolveOwnedShips(planet, request.ShipIds, playerId, out var ships);
        if (shipsError is not null)
        {
            return shipsError;
        }

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value;

        // Cargo additions (spec §2.3): null/both-zero skips validation; see ValidateCargo
        // for the negative/capacity/ownership/stored order.
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
            stream.AppendOne(planet.LoadCargoFromStorage(fleetId, cargo!.IronOre, cargo.IronIngot, now));
            fleetEvents.Add(new CargoLoaded(cargo.IronOre, cargo.IronIngot, now));
        }

        session.Events.StartStream<Fleet>(fleetId, [.. fleetEvents]);
        await session.SaveChangesAsync();

        var fleet = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(fleet!, t => balance.Ships.For(t).CargoCapacity));
    }

    // Disband (D6 counterpart): ships reach a roster only through this path. Allowed at
    // unowned/foreign planets (fleets.md); refused while cargo remains from #50 on.
    [WolverinePost("/api/fleets/{fleetId}/disband")]
    public static async Task<Results<Ok<FleetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Disband(
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
            return TypedResults.NotFound();
        }

        if (PlayerId(principal) != fleet.OwnerId)
        {
            return TypedResults.Forbid();
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Conflict("Only a stationed fleet can be disbanded.");
        }

        // D11 (#50): pre-validate here rather than let Fleet.Disband's own guard throw —
        // that guard is a defensive backstop (like Planet's cargo methods), not the 409 path;
        // without this check the endpoint previously surfaced an unhandled
        // InvalidOperationException as a 500 instead of the spec's 409.
        if (fleet.GetCargoLoad() > 0)
        {
            return TypedResults.Conflict("Cannot disband a fleet with cargo aboard.");
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

    // Manual unload (spec §4/§5, #50): retry unload for a stationed fleet at a planet the
    // caller owns. Complements the automatic Transport/Colonize unload for cargo left aboard
    // after a partial delivery (destination storage was full), and for Move fleets — which
    // never auto-unload — carrying cargo to their new location.
    [WolverinePost("/api/fleets/{fleetId}/unload")]
    public static async Task<Results<Ok<FleetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Unload(
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
            return TypedResults.NotFound();
        }

        if (PlayerId(principal) != fleet.OwnerId)
        {
            return TypedResults.Forbid();
        }

        if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
        {
            return TypedResults.Conflict("Only a stationed fleet can unload cargo.");
        }

        if (fleet.GetCargoLoad() == 0)
        {
            return TypedResults.Conflict("No cargo aboard.");
        }

        var planetStream = await session.Events.FetchForWriting<Planet>(fleet.LocationPlanetId.Value);
        var planet = planetStream.Aggregate
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        if (planet.OwnerId != PlayerId(principal))
        {
            return TypedResults.Forbid();
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
    public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, BadRequest<string>>> GetOwnFleets(
        ClaimsPrincipal principal,
        IQuerySession session,
        FleetStatus? status = null,
        int? page = null,
        int? pageSize = null)
    {
        var parameters = PaginationParameters.Create(
            page ?? PaginationParameters.DefaultPage,
            pageSize ?? PaginationParameters.DefaultPageSize);
        if (parameters is null)
        {
            return TypedResults.BadRequest("page and pageSize must be >= 1.");
        }

        var playerId = PlayerId(principal);
        var query = session.Query<Fleet>().Where(f => f.OwnerId == playerId);
        query = status is null
            ? query.Where(f => f.Status != FleetStatus.Disbanded)
            : query.Where(f => f.Status == status);

        var response = await query
            .OrderBy(f => f.AssembledAt).ThenBy(f => f.Id)
            .ToPagedResponseAsync(parameters,
                f => new FleetSummaryResponse(f.Id, f.OwnerId, f.Status, f.LocationPlanetId, f.AssembledAt, f.Ships.Count));
        return TypedResults.Ok(response);
    }

    // Universe-visible (full visibility, no fog of war in MVP).
    [WolverineGet("/api/fleets/{fleetId}")]
    public static async Task<Results<Ok<FleetResponse>, NotFound>> GetFleet(
        Guid fleetId, IQuerySession session, IOptions<BalanceOptions> balanceOptions)
    {
        var fleet = await session.LoadAsync<Fleet>(fleetId);
        if (fleet is null)
        {
            return TypedResults.NotFound();
        }

        var balance = balanceOptions.Value;
        return TypedResults.Ok(FleetResponse.From(fleet, t => balance.Ships.For(t).CargoCapacity));
    }

    // Universe-visible: fleets currently stationed at this planet.
    [WolverineGet("/api/planets/{planetId}/fleets")]
    public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, NotFound, BadRequest<string>>> GetPlanetFleets(
        Guid planetId,
        IQuerySession session,
        int? page = null,
        int? pageSize = null)
    {
        var planet = await session.LoadAsync<Planet>(planetId);
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        var parameters = PaginationParameters.Create(
            page ?? PaginationParameters.DefaultPage,
            pageSize ?? PaginationParameters.DefaultPageSize);
        if (parameters is null)
        {
            return TypedResults.BadRequest("page and pageSize must be >= 1.");
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
    private static Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>? ValidateLaunchRequest(
        LaunchMissionRequest request)
    {
        if (!Enum.IsDefined(request.Mission))
        {
            return TypedResults.BadRequest("Unknown mission type.");
        }

        if (request.Mission == MissionType.Colonize)
        {
            return TypedResults.BadRequest("Mission not supported yet.");   // Colonize → #51
        }

        if (request.DestinationPlanetId == Guid.Empty)
        {
            return TypedResults.BadRequest("destinationPlanetId is required.");
        }

        return null;
    }

    // Resolves the requested ship ids against the planet's roster (409 if any are missing)
    // and checks the caller owns every one of them (403, per D13). Returns null and the
    // resolved ships via `out` when valid; kept as its own method so Assemble stays within
    // MA0051's line limit.
    private static Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>? ResolveOwnedShips(
        Planet planet, IReadOnlyList<Guid> shipIds, Guid? playerId, out List<RosterShip> ships)
    {
        var byId = planet.Ships.ToDictionary(s => s.Id);
        var missing = shipIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            ships = [];
            return TypedResults.Conflict($"Ship(s) not on this planet's roster: {string.Join(", ", missing)}.");
        }

        ships = shipIds.Select(id => byId[id]).ToList();
        if (playerId is null || ships.Any(s => s.OwnerId != playerId))
        {
            return TypedResults.Forbid();
        }

        return null;
    }

    // Cargo checks for Assemble (spec §2.3, in order: negative → over the selected ships'
    // combined capacity → planet not owned by the caller → over what's actually stored).
    // Returns null when the request is valid; kept as its own method so Assemble stays
    // within MA0051's line limit.
    private static Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>? ValidateCargo(
        CargoRequest cargo,
        IReadOnlyList<RosterShip> ships,
        Planet planet,
        Guid? playerId,
        BalanceOptions balance,
        DateTimeOffset now)
    {
        if (cargo.IronOre < 0m || cargo.IronIngot < 0m)
        {
            return TypedResults.BadRequest("Cargo amounts cannot be negative.");
        }

        var capacity = ships.Sum(s => balance.Ships.For(s.Type).CargoCapacity);
        if (cargo.IronOre + cargo.IronIngot > capacity)
        {
            return TypedResults.BadRequest("Cargo exceeds the selected ships' combined capacity.");
        }

        if (planet.OwnerId != playerId)
        {
            return TypedResults.Forbid();
        }

        if (cargo.IronOre > planet.IronOre.GetCurrentValue(now) || cargo.IronIngot > planet.IronIngot.GetCurrentValue(now))
        {
            return TypedResults.Conflict("Insufficient stored resources for the requested cargo.");
        }

        return null;
    }

    private static Guid? PlayerId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
