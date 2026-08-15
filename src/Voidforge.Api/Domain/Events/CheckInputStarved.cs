namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at PredictedAt — the instant the stored
// IronOre buffer was predicted to empty while a refinery drains it. The handler re-derives input
// starvation at this scheduled time (validate-on-arrival): if ore has returned (a drill resumed / was
// built, or the buffer is not actually empty), it is a no-op. Sibling of CheckPoolDepleted.
public sealed record CheckInputStarved(Guid PlanetId, DateTimeOffset PredictedAt);
