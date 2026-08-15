namespace Voidforge.Api.Domain.Events;

// Durable Wolverine scheduled message (ADR 0001), delivered at PredictedAt — the instant the stored
// IronIngot buffer was predicted to empty while in-flight builds (UnderConstruction buildings + Active
// ship builds) drain it. The handler re-derives ingot starvation at this scheduled time
// (validate-on-arrival): if ingots have returned (a refinery resumed producing, or the buffer is not
// actually empty), it is a no-op. The ingot-consumer sibling of CheckInputStarved.
public sealed record CheckIngotStarved(Guid PlanetId, DateTimeOffset PredictedAt);
