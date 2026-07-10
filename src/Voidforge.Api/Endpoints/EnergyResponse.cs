namespace Voidforge.Api.Endpoints;

// Energy is a flow resource: computed from the operational building composition at
// request time, not stored. Nested block on PlanetResponse, reusable by the planet
// summary DTO planned in #30.
public sealed record EnergyResponse(
    decimal GenerationMw,
    decimal ConsumptionMw,
    decimal ProductivityMultiplier);
