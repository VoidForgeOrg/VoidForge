using Voidforge.Api.Domain;

namespace Voidforge.SoakTests;

// The single authoritative, post-drain read of world state — every list materialized at one fixed
// instant (<see cref="Now"/>) so cross-pool relations are consistent — plus the driver's recorded
// HTTP statuses (I4/I5) and the during-run deposit series (I11).
public sealed record WorldSnapshot(
    IReadOnlyList<Planet> Planets,
    IReadOnlyList<Fleet> Fleets,
    IReadOnlyList<Player> Players,
    DateTimeOffset Now,
    long DeadLetterCount,
    IReadOnlyList<int> HttpStatuses,
    IReadOnlyList<IntermediateSnapshot> DepositSeries);
