using Voidforge.Api.Domain;
using Voidforge.Api.Travel;
using Xunit;

namespace Voidforge.Tests.Travel;

public sealed class LinearTravelPlannerTests
{
    private static readonly LinearTravelPlanner _planner = new();

    [Fact]
    public void ThreeDimensionalDistance()
    {
        // origin (0,0,0) -> destination (2,3,6) is distance 7
        // speed 3.5 -> arrival departAt + 2s
        var origin = new Coordinates(0, 0, 0);
        var destination = new Coordinates(2, 3, 6);
        var departAt = DateTimeOffset.UtcNow;
        var speedPerSecond = 3.5m;

        var plan = _planner.Plan(origin, destination, speedPerSecond, departAt);

        Assert.Equal(7m, plan.TotalDistance);
        Assert.Equal(departAt.AddSeconds(2), plan.ArrivesAt);
        Assert.Single(plan.Legs);

        var leg = plan.Legs[0];
        Assert.Null(leg.WaypointPlanetId);
        Assert.Equal(7m, leg.Distance);
        Assert.Equal(departAt.AddSeconds(2), leg.ArrivesAt);
    }

    [Fact]
    public void ZeroDistance()
    {
        var origin = new Coordinates(0, 0, 0);
        var destination = new Coordinates(0, 0, 0);
        var departAt = DateTimeOffset.UtcNow;
        var speedPerSecond = 5m;

        var plan = _planner.Plan(origin, destination, speedPerSecond, departAt);

        Assert.Equal(0m, plan.TotalDistance);
        Assert.Equal(departAt, plan.ArrivesAt);
    }

    [Fact]
    public void NonPositiveSpeedThrows()
    {
        var origin = new Coordinates(0, 0, 0);
        var destination = new Coordinates(1, 1, 1);
        var departAt = DateTimeOffset.UtcNow;

        Assert.Throws<InvalidOperationException>(
            () => _planner.Plan(origin, destination, 0m, departAt));

        Assert.Throws<InvalidOperationException>(
            () => _planner.Plan(origin, destination, -1m, departAt));
    }
}
