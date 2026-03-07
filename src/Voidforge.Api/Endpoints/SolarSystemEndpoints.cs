using Marten;
using Voidforge.Api.Documents;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class SolarSystemEndpoints
{
    [WolverineGet("/api/solar-systems")]
    public static async Task<IReadOnlyList<SolarSystemResponse>> GetAll(IQuerySession session)
    {
        var systems = await session.Query<SolarSystem>().ToListAsync();

        return systems.Select(s => new SolarSystemResponse(
            s.Id,
            s.Name,
            s.X,
            s.Y,
            s.Z,
            s.PlanetIds.ToList().AsReadOnly())).ToList();
    }
}
