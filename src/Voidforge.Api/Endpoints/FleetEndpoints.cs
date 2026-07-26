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
    // Launch (#49, Move only — Transport/Colonize dispatch land in #50/#51). Only the Fleet
    // stream is touched (spec §2.3); the origin and destination planets are read for
    // coordinates, never appended to. Arrival resolves durably via CompleteFleetArrival
    // (ADR 0001), scheduled here and handled by CompleteFleetArrivalHandler.
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
        if (request.Mission != MissionType.Move)
        {
            return TypedResults.BadRequest("Mission not supported yet.");   // Transport → #50, Colonize → #51
        }

        if (request.DestinationPlanetId == Guid.Empty)
        {
            return TypedResults.BadRequest("destinationPlanetId is required.");
        }

        var stream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = stream.Aggregate;
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
        return TypedResults.Ok(FleetResponse.From(updated!));
    }

    // Assembly (spec §2.3): one transaction over both streams. Ship ownership — not planet
    // ownership — is what's validated (D13): ships stranded on a foreign or unowned world
    // can still be formed into a fleet by their owner. Cargo loading arrives with #50.
    [WolverinePost("/api/planets/{planetId}/fleets")]
    public static async Task<Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>> Assemble(
        Guid planetId,
        AssembleFleetRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
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

        var byId = planet.Ships.ToDictionary(s => s.Id);
        var missing = request.ShipIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return TypedResults.Conflict($"Ship(s) not on this planet's roster: {string.Join(", ", missing)}.");
        }

        var playerId = PlayerId(principal);
        var ships = request.ShipIds.Select(id => byId[id]).ToList();
        if (playerId is null || ships.Any(s => s.OwnerId != playerId))
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var fleetId = Guid.NewGuid();
        stream.AppendOne(planet.RemoveShipsFromRoster(fleetId, request.ShipIds, now));
        session.Events.StartStream<Fleet>(fleetId, Fleet.Assemble(playerId.Value, planetId, ships, now));
        await session.SaveChangesAsync();

        var fleet = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(fleet!));
    }

    // Disband (D6 counterpart): ships reach a roster only through this path. Allowed at
    // unowned/foreign planets (fleets.md); refused while cargo remains from #50 on.
    [WolverinePost("/api/fleets/{fleetId}/disband")]
    public static async Task<Results<Ok<FleetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Disband(
        Guid fleetId,
        ClaimsPrincipal principal,
        IDocumentSession session,
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

        var planetStream = await session.Events.FetchForWriting<Planet>(fleet.LocationPlanetId.Value);
        var planet = planetStream.Aggregate
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        var now = timeProvider.GetUtcNow();
        planetStream.AppendOne(planet.ReturnShipsToRoster(fleet.Id, fleet.ToRosterShips(), now));
        fleetStream.AppendOne(fleet.Disband(now));
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!));
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
    public static async Task<Results<Ok<FleetResponse>, NotFound>> GetFleet(Guid fleetId, IQuerySession session)
    {
        var fleet = await session.LoadAsync<Fleet>(fleetId);
        return fleet is null ? TypedResults.NotFound() : TypedResults.Ok(FleetResponse.From(fleet));
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

    private static Guid? PlayerId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
