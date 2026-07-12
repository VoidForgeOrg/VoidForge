using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record ShipBuildResponse(Guid Id, ShipType Type, ShipBuildStatus Status, DateTimeOffset? EtaCompletionUtc);
