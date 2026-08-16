using Voidforge.Api.Travel;

namespace Voidforge.Api.Domain.Events;

// Recall (#73, D10): an in-transit fleet turns around to head back to its origin. The
// synthesized return plan rides the event so replay is deterministic — an in-transit fleet
// has no live position to re-plan from (mirrors FleetDeparted.Plan). RecalledAt both marks
// the departure of the return leg and doubles as the "already returning" 409 guard.
public sealed record FleetRecalled(TravelPlan ReturnPlan, DateTimeOffset RecalledAt);
