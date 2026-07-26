using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record RosterShipResponse(Guid Id, ShipType Type, DateTimeOffset CompletedAt, Guid? OwnerId);
