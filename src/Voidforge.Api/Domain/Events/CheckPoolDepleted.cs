namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at PredictedAt — the instant the finite
// ore deposit was predicted to empty. The handler re-derives depletion at this scheduled time
// (validate-on-arrival): if rates changed since prediction (drills removed/halted) and the deposit
// is not actually empty, it is a no-op. Sibling of CheckStorageFull.
public sealed record CheckPoolDepleted(Guid PlanetId, DateTimeOffset PredictedAt);
