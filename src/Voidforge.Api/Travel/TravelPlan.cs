namespace Voidforge.Api.Travel;

public sealed record TravelPlan(
    DateTimeOffset ArrivesAt,
    decimal TotalDistance,
    IReadOnlyList<TravelLeg> Legs);
