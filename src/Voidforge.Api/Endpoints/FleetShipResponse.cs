using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record FleetShipResponse(Guid Id, ShipType Type, DateTimeOffset CompletedAt);
