using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Auth;
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
    public static async Task<Results<Ok<ShipBuildResponse>, ProblemHttpResult>> Queue(
        Guid planetId,
        QueueShipRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        IOptions<BalanceOptions> balanceOptions,
        TimeProvider timeProvider)
    {
        // FetchForWriting arms Marten's optimistic-concurrency guard from the fetched stream version.
        // A losing append fails on commit with a ConcurrencyException, mapped to 409 by
        // ConcurrencyConflictExceptionHandler (the commit is issued by Wolverine's transactional
        // middleware after this method returns, so it cannot be caught here).
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (principal.PlayerId() is not { } playerId || !planet.IsOwnedBy(playerId))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value.ForShip(request.ShipType);
        var buildId = Guid.NewGuid();

        var events = planet.QueueShip(request.ShipType, now, buildId, balance.DrainPerSecond, balance.BuildDurationSeconds);
        stream.AppendMany([.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, planetId, events);
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Planet>(planetId);
        // An active ship build drains ingots, changing the ingot fill deadline — reschedule all
        // cascade checks from the post-commit state (#69/#70).
        await StorageHaltScheduling.ScheduleAllChecksAsync(bus, planetId, updated!, now);
        var build = updated!.ShipQueue.Single(b => b.Id == buildId);
        return TypedResults.Ok(new ShipBuildResponse(build.Id, build.Type, build.Status, build.CompletesAt));
    }

    [WolverineDelete("/api/planets/{planetId}/ship-queue/{buildId}")]
    public static async Task<Results<Ok<PlanetResponse>, ProblemHttpResult>> Cancel(
        Guid planetId,
        Guid buildId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        IMessageBus bus,
        TimeProvider timeProvider)
    {
        // FetchForWriting arms Marten's optimistic-concurrency guard from the fetched stream version.
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        if (principal.PlayerId() is not { } playerId || !planet.IsOwnedBy(playerId))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        var now = timeProvider.GetUtcNow();
        var events = planet.CancelShipBuild(buildId, now);
        if (events.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);   // unknown build id
        }

        stream.AppendMany([.. events]);
        await ShipConstructionScheduling.ScheduleStartedBuildsAsync(bus, planetId, events);
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Planet>(planetId);
        return TypedResults.Ok(PlanetResponse.From(updated!, now));
    }

    // Active builds first (with ETA), then queued FIFO. Paginated per the #29 contract.
    [WolverineGet("/api/planets/{planetId}/ship-queue")]
    public static async Task<Results<Ok<PagedResponse<ShipBuildResponse>>, ProblemHttpResult>> GetQueue(
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
    public static async Task<Results<Ok<PagedResponse<RosterShipResponse>>, ProblemHttpResult>> GetRoster(
        Guid planetId,
        IQuerySession session,
        ShipType? type = null,
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

        IReadOnlyList<RosterShip> ordered = planet.Ships
            .Where(s => type is null || s.Type == type)
            .OrderBy(s => s.CompletedAt)
            .ThenBy(s => s.Id)
            .ToList();

        var response = ordered.ToPagedResponse(parameters,
            s => new RosterShipResponse(s.Id, s.Type, s.CompletedAt, s.OwnerId));
        return TypedResults.Ok(response);
    }
}
