using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Domain;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class PlanetEndpoints
{
    [WolverineGet("/api/planets/{id}")]
    public static async Task<Results<Ok<PlanetResponse>, NotFound>> GetById(
        Guid id,
        IQuerySession session,
        TimeProvider timeProvider)
    {
        var planet = await session.LoadAsync<Planet>(id);

        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(PlanetResponse.From(planet, timeProvider.GetUtcNow()));
    }
}
