using Voidforge.Api.Domain;

namespace Voidforge.Api.Travel;

/// <summary>
/// Linear travel planner for MVP. Post-MVP lanes and jump gates will land as additional planner
/// implementations without reshaping the event model or endpoint contracts (spec D3/D4).
/// </summary>
public sealed class LinearTravelPlanner : ITravelPlanner
{
    public TravelPlan Plan(Coordinates origin, Coordinates destination,
                          decimal speedPerSecond, DateTimeOffset departAt)
    {
        if (speedPerSecond <= 0)
        {
            throw new InvalidOperationException("Speed must be positive.");
        }

        var dx = destination.X - origin.X;
        var dy = destination.Y - origin.Y;
        var dz = destination.Z - origin.Z;

        var distance = (decimal)Math.Sqrt((double)(dx * dx + dy * dy + dz * dz));
        var seconds = distance / speedPerSecond;
        var arrivesAt = departAt.AddSeconds((double)seconds);

        var leg = new TravelLeg(WaypointPlanetId: null, Distance: distance, ArrivesAt: arrivesAt);
        var plan = new TravelPlan(ArrivesAt: arrivesAt, TotalDistance: distance, Legs: [leg]);

        return plan;
    }
}
