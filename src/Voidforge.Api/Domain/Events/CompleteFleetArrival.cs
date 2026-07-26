namespace Voidforge.Api.Domain.Events;

public sealed record CompleteFleetArrival(Guid FleetId, DateTimeOffset ArrivesAt);
