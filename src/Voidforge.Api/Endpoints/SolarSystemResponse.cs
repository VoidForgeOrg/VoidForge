namespace Voidforge.Api.Endpoints;

public sealed record SolarSystemResponse(
    Guid Id,
    string Name,
    decimal X,
    decimal Y,
    decimal Z,
    IReadOnlyList<Guid> PlanetIds);
