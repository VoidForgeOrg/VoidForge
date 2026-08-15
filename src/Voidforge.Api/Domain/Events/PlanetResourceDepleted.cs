namespace Voidforge.Api.Domain.Events;

// The finite ore deposit reached zero (#70). Emitted by Planet.EvaluateDepletion alongside one
// BuildingHalted(ResourceDepleted) per operational Drill. Apply pins the deposit's checkpoint to 0
// at the depletion instant; the drill halts (separate events) each drop their Drill out of the
// Operational set, so RebaseRates re-derives oreInflow → 0 and the deposit's drain Rate → 0.
public sealed record PlanetResourceDepleted(ResourceType Resource, DateTimeOffset At);
