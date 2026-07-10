using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Pagination;
using Wolverine;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class ShipEndpoints
{
    [WolverinePost("/api/planets/{planetId}/ship-queue")]
    public static async Task<Results<Ok<ShipBuildResponse>, NotFound, ForbidHttpResult>> Queue(
        Guid planetId,
        QueueShipRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        var planet = await session.LoadAsync<Planet>(planetId);
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        if (!IsOwner(principal, planet))
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value.ForShip(request.ShipType);
        var buildId = Guid.NewGuid();

        var events = planet.QueueShip(request.ShipType, now, buildId, balance.DrainPerSecond, balance.BuildDurationSeconds);
        session.Events.Append(planetId, [.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, planetId, events);
        await session.SaveChangesAsync();

        var updated = await session.LoadAsync<Planet>(planetId);
        var build = updated!.ShipQueue.Single(b => b.Id == buildId);
        return TypedResults.Ok(new ShipBuildResponse(build.Id, build.Type, build.Status, build.CompletesAt));
    }

    [WolverineDelete("/api/planets/{planetId}/ship-queue/{buildId}")]
    public static async Task<Results<Ok<PlanetResponse>, NotFound, ForbidHttpResult>> Cancel(
        Guid planetId,
        Guid buildId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        TimeProvider timeProvider)
    {
        var planet = await session.LoadAsync<Planet>(planetId);
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        if (!IsOwner(principal, planet))
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var events = planet.CancelShipBuild(buildId, now);
        if (events.Count == 0)
        {
            return TypedResults.NotFound();   // unknown build id
        }

        session.Events.Append(planetId, [.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, planetId, events);
        await session.SaveChangesAsync();

        var updated = await session.LoadAsync<Planet>(planetId);
        return TypedResults.Ok(PlanetResponse.From(updated!, now));
    }

    // Active builds first (with ETA), then queued FIFO. Paginated per the #29 contract.
    [WolverineGet("/api/planets/{planetId}/ship-queue")]
    public static async Task<Results<Ok<PagedResponse<ShipBuildResponse>>, NotFound, BadRequest<string>>> GetQueue(
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

        IReadOnlyList<ShipBuild> ordered = planet.ShipQueue
            .OrderByDescending(b => b.Status == ShipBuildStatus.Active)   // active first
            .ThenBy(b => b.QueuedAt)                                      // then FIFO
            .ToList();

        var response = ordered.ToPagedResponse(parameters,
            b => new ShipBuildResponse(b.Id, b.Type, b.Status, b.CompletesAt));
        return TypedResults.Ok(response);
    }

    // Completed-ship roster, optionally filtered by type, sorted (CompletedAt, Id). Paginated.
    [WolverineGet("/api/planets/{planetId}/ships")]
    public static async Task<Results<Ok<PagedResponse<RosterShipResponse>>, NotFound, BadRequest<string>>> GetRoster(
        Guid planetId,
        IQuerySession session,
        ShipType? type = null,
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

        IReadOnlyList<RosterShip> ordered = planet.Ships
            .Where(s => type is null || s.Type == type)
            .OrderBy(s => s.CompletedAt)
            .ThenBy(s => s.Id)
            .ToList();

        var response = ordered.ToPagedResponse(parameters,
            s => new RosterShipResponse(s.Id, s.Type, s.CompletedAt));
        return TypedResults.Ok(response);
    }

    private static bool IsOwner(ClaimsPrincipal principal, Planet planet)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var playerId) && planet.OwnerId == playerId;
    }
}
