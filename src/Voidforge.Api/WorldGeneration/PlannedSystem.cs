using Voidforge.Api.Documents;

namespace Voidforge.Api.WorldGeneration;

// One solar system in the pure output of WorldSeeder.BuildWorld — the SolarSystem document plus its
// planets. Decoupled from any Marten session so world generation can be unit-tested for determinism.
internal sealed record PlannedSystem(SolarSystem System, IReadOnlyList<PlannedPlanet> Planets);
