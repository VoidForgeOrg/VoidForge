namespace Voidforge.Api.Endpoints;

public sealed record AssembleFleetRequest(IReadOnlyList<Guid> ShipIds);
