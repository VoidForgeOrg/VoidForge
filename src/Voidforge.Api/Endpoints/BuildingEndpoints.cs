using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.WorldGeneration;
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
        IOptions<WorldGenOptions> worldGenOptions)
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

        if (planet.Buildings.Count >= planet.BuildingSlotCount)
        {
            return TypedResults.Conflict("No available building slots on this planet.");
        }

        var now = DateTimeOffset.UtcNow;
        var extractionRate = request.BuildingType == BuildingType.Drill
            ? worldGenOptions.Value.DrillExtractionRate
            : 0;

        session.Events.Append(planetId, new BuildingPlaced(request.BuildingType, extractionRate, now));
        await session.SaveChangesAsync();

        var updated = await session.LoadAsync<Planet>(planetId);
        return TypedResults.Ok(PlanetResponse.From(updated!, now));
    }
}
