namespace Voidforge.Api.Endpoints;

// Cargo defaults to null so existing callers that only ever supplied ShipIds keep compiling
// unchanged (#50 adds cargo loading at assembly, spec §2.3).
public sealed record AssembleFleetRequest(IReadOnlyList<Guid> ShipIds, CargoRequest? Cargo = null);
