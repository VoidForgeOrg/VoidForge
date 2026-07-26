using Voidforge.Api.Travel;

namespace Voidforge.Api.Domain.Events;

// The plan rides the event so in-flight fleets keep the departure economics they launched
// under even if balance or the travel planner changes mid-flight (Phase 3 D10 principle —
// the same reasoning that puts DrainPerSecond on ShipConstructionQueued).
public sealed record FleetDeparted(
    Guid OriginPlanetId, Guid DestinationPlanetId, MissionType Mission, DateTimeOffset DepartedAt, TravelPlan Plan);
