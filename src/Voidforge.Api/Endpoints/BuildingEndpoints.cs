using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Balance;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class BuildingEndpoints
{
    // Placement starts construction (#26): the slot is taken immediately as UnderConstruction
    // and ingots drain over cost/duration; a durable CompleteBuildingConstruction message is
    // scheduled at the completion time (ADR 0001).
    [WolverinePost("/api/planets/{planetId}/buildings")]
    public static async Task<Results<Ok<PlanetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Place(
        Guid planetId,
        PlaceBuildingRequest request,
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
            return TypedResults.NotFound();
        }

        if (!IsOwner(principal, planet))
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var balance = balanceOptions.Value.ForBuilding(request.BuildingType);

        BuildingConstructionStarted started;
        try
        {
            started = planet.StartConstruction(request.BuildingType, now, balance.IngotCost, balance.BuildDurationSeconds);
        }
        catch (NoFreeSlotsException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        stream.AppendOne(started);
        // Schedule completion through the Marten transactional outbox (persisted with this
        // transaction; survives restart). Validate-on-arrival makes redelivery safe.
        await bus.ScheduleAsync(
            new CompleteBuildingConstruction(planetId, started.SlotIndex, started.CompletesAt),
            started.CompletesAt);
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Planet>(planetId);
        // A new UnderConstruction slot changes ingot-drain rates now, and its eventual completion
        // changes production rates — reschedule all cascade checks from the post-commit state (#69/#70).
        await StorageHaltScheduling.ScheduleAllChecksAsync(bus, planetId, updated!, now);
        return TypedResults.Ok(PlanetResponse.From(updated!, now));
    }

    // Cancel an in-progress construction (#72): no refund; the slot becomes a Cancelled tombstone,
    // keeping its list position so SlotIndex stays stable and any in-flight
    // CompleteBuildingConstruction message finds the tombstone and no-ops (validate-on-arrival).
    // 204 with no body — nothing meaningful to return. Per plan decision 4, no resume hook is wired:
    // a cancelled ingot drain has nothing to un-halt in #72's world (deferred to #83).
    [WolverineDelete("/api/planets/{planetId}/buildings/{slotIndex}/construction")]
    public static async Task<Results<NoContent, NotFound, ForbidHttpResult, Conflict<string>>> CancelConstruction(
        Guid planetId,
        int slotIndex,
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
            return TypedResults.NotFound();
        }

        if (!IsOwner(principal, planet))
        {
            return TypedResults.Forbid();
        }

        // SlotIndex addresses the append-only Buildings list, so range is against the raw count; a
        // tombstoned slot is in range but its status is not UnderConstruction, handled below as 409.
        if (slotIndex < 0 || slotIndex >= planet.Buildings.Count)
        {
            return TypedResults.NotFound();
        }

        if (planet.Buildings[slotIndex].Status != BuildingStatus.UnderConstruction)
        {
            return TypedResults.Conflict("Only in-progress construction can be cancelled.");
        }

        var now = timeProvider.GetUtcNow();
        stream.AppendMany([.. planet.CancelConstruction(slotIndex, now)]);
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Planet>(planetId);
        // Removing the construction ingot drain raises the ingot fill rate — reschedule all cascade
        // checks from the post-commit state (#69/#70).
        await StorageHaltScheduling.ScheduleAllChecksAsync(bus, planetId, updated!, now);
        return TypedResults.NoContent();
    }

    private static bool IsOwner(ClaimsPrincipal principal, Planet planet)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var playerId) && planet.OwnerId == playerId;
    }
}
