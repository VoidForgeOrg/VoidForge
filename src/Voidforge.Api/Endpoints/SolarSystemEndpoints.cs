using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Documents;
using Voidforge.Api.Pagination;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class SolarSystemEndpoints
{
    // Paginated per the #29 contract. Deterministic order by Name so paging is stable.
    // Nullable int params distinguish "omitted" (null → default) from "explicitly zero"
    // (0 → rejected as invalid) so Wolverine's missing-param-as-null binding works correctly.
    [WolverineGet("/api/solar-systems")]
    public static async Task<Results<Ok<PagedResponse<SolarSystemResponse>>, BadRequest<string>>> GetAll(
        IQuerySession session,
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

        var response = await session.Query<SolarSystem>()
            .OrderBy(s => s.Name)
            .ToPagedResponseAsync(parameters, s => new SolarSystemResponse(
                s.Id,
                s.Name,
                s.X,
                s.Y,
                s.Z,
                s.PlanetIds.ToList().AsReadOnly()));

        return TypedResults.Ok(response);
    }
}
