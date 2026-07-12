namespace Voidforge.Api.Domain;

// A ship in the planet's single build queue. DrainPerSecond + BuildDurationSeconds are set at
// enqueue (from balance config, by the endpoint) and carried so auto-start — which happens
// inside pure aggregate methods — needs no config. StartedAt/CompletesAt are set when it starts.
public sealed record ShipBuild(
    Guid Id,
    ShipType Type,
    ShipBuildStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletesAt,
    decimal DrainPerSecond,
    decimal BuildDurationSeconds);
