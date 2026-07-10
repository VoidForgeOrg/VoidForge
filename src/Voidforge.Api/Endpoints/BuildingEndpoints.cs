using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class BuildingEndpoints
{
    // Phase 2: placement is instant and free. Construction time, costs, and energy arrive in Phase 3.
    [WolverinePost("/api/planets/{planetId}/buildings")]
    public static async Task<Results<Ok<PlanetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Place(
        Guid planetId,
        PlaceBuildingRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        TimeProvider timeProvider)
    {
        var planet = await session.LoadAsync<Planet>(planetId);
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var playerId) || planet.OwnerId != playerId)
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();

        BuildingPlaced placed;
        try
        {
            // The slot-availability invariant lives in the domain; the endpoint maps its
            // violation to a 409 response.
            placed = planet.PlaceBuilding(request.BuildingType, now);
        }
        catch (NoFreeSlotsException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        session.Events.Append(planetId, placed);
        await session.SaveChangesAsync();

        var updated = await session.LoadAsync<Planet>(planetId);
        return TypedResults.Ok(PlanetResponse.From(updated!, now));
    }
}
