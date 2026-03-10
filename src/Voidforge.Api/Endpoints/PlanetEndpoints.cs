using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Domain;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class PlanetEndpoints
{
    [WolverineGet("/api/planets/{id}")]
    public static async Task<Results<Ok<PlanetResponse>, NotFound>> GetById(Guid id, IQuerySession session)
    {
        var planet = await session.LoadAsync<Planet>(id);

        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        return TypedResults.Ok(new PlanetResponse(
            planet.Id,
            planet.Name,
            planet.SolarSystemId,
            planet.OwnerId,
            planet.IronOrePool,
            planet.BuildingSlotCount,
            new ResourcePoolResponse(
                planet.IronOre.GetCurrentValue(now),
                planet.IronOre.Rate,
                planet.IronOre.StorageCapacity),
            new ResourcePoolResponse(
                planet.IronIngot.GetCurrentValue(now),
                planet.IronIngot.Rate,
                planet.IronIngot.StorageCapacity)));
    }
}
