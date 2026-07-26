using Voidforge.Api.Domain;

namespace Voidforge.Api.Travel;

public interface ITravelPlanner
{
    TravelPlan Plan(Coordinates origin, Coordinates destination,
                    decimal speedPerSecond, DateTimeOffset departAt);
}
