using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.WorldGeneration;

// One planet in the pure output of WorldSeeder.BuildWorld — its stream id alongside the
// PlanetCreated event StartStream needs. Decoupled from any Marten session so world generation
// can be unit-tested for determinism.
internal sealed record PlannedPlanet(Guid PlanetId, PlanetCreated Event);
