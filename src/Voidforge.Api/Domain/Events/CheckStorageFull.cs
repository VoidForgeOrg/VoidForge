namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at PredictedAt — the instant a
// resource pool was predicted to reach capacity. The handler re-derives halts at this scheduled
// time (validate-on-arrival): if rates changed since prediction and nothing is actually full, it
// is a no-op. ResourceType lives in the parent Voidforge.Api.Domain namespace.
public sealed record CheckStorageFull(Guid PlanetId, ResourceType Resource, DateTimeOffset PredictedAt);
