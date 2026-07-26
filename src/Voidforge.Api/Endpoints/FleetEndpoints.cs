using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Domain;
using Voidforge.Api.Pagination;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class FleetEndpoints
{
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
        if (request.ShipIds.Count == 0)
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
