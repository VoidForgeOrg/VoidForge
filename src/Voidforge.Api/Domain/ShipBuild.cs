namespace Voidforge.Api.Domain;

// A ship in the planet's single build queue. DrainPerSecond + BuildDurationSeconds are set at
// enqueue (from balance config, by the endpoint) and carried so auto-start — which happens
// inside pure aggregate methods — needs no config. StartedAt/CompletesAt are set when it starts.
// HaltedAt is set only while Halted (the instant the build paused on zero ingots, #83) — CompletesAt
// is RETAINED so resume can recompute the remaining work (CompletesAt − HaltedAt) — and cleared on
// resume.
public sealed record ShipBuild(
    Guid Id,
    ShipType Type,
    ShipBuildStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletesAt,
    decimal DrainPerSecond,
    decimal BuildDurationSeconds,
    DateTimeOffset? HaltedAt = null);
