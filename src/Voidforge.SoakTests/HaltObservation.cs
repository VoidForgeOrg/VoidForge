using Voidforge.Api.Domain;

namespace Voidforge.SoakTests;

// One halted building observed during the run — its reason is what Tier 3's O6 cascade check keys on.
// Captured over time (not just at the final snapshot) so a TRANSIENT halt, e.g. an OutputStorageFull
// that later resolves, is not missed by the single post-drain read.
public sealed record HaltObservation(Guid PlanetId, HaltReason Reason);
