using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Scoring;
using Xunit;

namespace Voidforge.Tests.Scoring;

// Pure-domain unit tests (no host, no DB) mirroring PlanetHaltingTests: fixed base time, aggregates
// built in-memory. Exact-value assertions are safe here — pools are fixed and evaluated at a fixed
// `now`, no live accrual. Expected values are reproduced from ScoringSpecs constants (never magic
// numbers) so a placeholder change can't silently invalidate a test's intent.
[Trait("Category", "Unit")]
public sealed class ScoreCalculatorTests
{
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _owner = Guid.NewGuid();

    private static Planet NewOwnedPlanet()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 50000, 6, 10000, 5000, 0m, 0m, 0m));
        planet.Apply(new PlanetColonized(_owner, 0, 0, _at));
        return planet;
    }

    private static Fleet NewStationedFleet(params FleetShip[] ships) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = _owner,
        Status = FleetStatus.Stationed,
        Ships = [.. ships],
    };

    private static ShipBuild Build(ShipType type, ShipBuildStatus status) =>
        new(Guid.NewGuid(), type, status, _at, _at, _at.AddSeconds(10), 1m, 10m);

    // One planet with 5 countable slots (Drill x2, Refinery, Generator, Shipyard) + 2 tombstones that
    // must be excluded, a roster CargoVessel, a 3-entry queue (2 ColonyShip + 1 CargoVessel), and
    // storage pools read at `now = _at + 10s` → ore 100+5*10=150, ingot 40+2*10=60.
    private static Planet BuildRichPlanet()
    {
        var planet = NewOwnedPlanet();
        planet.Buildings =
        [
            new BuildingSlot(BuildingType.Drill, BuildingStatus.Operational),
            new BuildingSlot(BuildingType.Refinery, BuildingStatus.UnderConstruction),
            new BuildingSlot(BuildingType.Generator, BuildingStatus.Halted),
            new BuildingSlot(BuildingType.Shipyard, BuildingStatus.ConstructionHalted),
            new BuildingSlot(BuildingType.Drill, BuildingStatus.Demolishing),
            new BuildingSlot(BuildingType.Drill, BuildingStatus.Cancelled),     // tombstone — excluded
            new BuildingSlot(BuildingType.Refinery, BuildingStatus.Demolished), // tombstone — excluded
        ];
        planet.Ships = [new RosterShip(Guid.NewGuid(), ShipType.CargoVessel, _at, _owner)];
        planet.ShipQueue =
        [
            Build(ShipType.ColonyShip, ShipBuildStatus.Active),
            Build(ShipType.CargoVessel, ShipBuildStatus.Queued),
            Build(ShipType.ColonyShip, ShipBuildStatus.Halted),
        ];
        planet.IronOre = new ResourcePool(100m, 5m, 10000m, _at);
        planet.IronIngot = new ResourcePool(40m, 2m, 5000m, _at);
        return planet;
    }

    // A stationed fleet: 1 ColonyShip + 1 CargoVessel in flight, plus cargo aboard.
    private static Fleet BuildRichFleet()
    {
        var fleet = NewStationedFleet(
            new FleetShip(Guid.NewGuid(), ShipType.ColonyShip, _at),
            new FleetShip(Guid.NewGuid(), ShipType.CargoVessel, _at));
        fleet.CargoIronOre = 20m;
        fleet.CargoIronIngot = 10m;
        return fleet;
    }

    // The comprehensive exact-value test: one building of each countable status (plus the two tombstones,
    // which must be excluded), ships across all three sources (no double-count), and resources evaluated
    // at `now` (later than the checkpoint, with positive rates).
    [Fact]
    public void ComputeReturnsExactScoreAcrossEveryAssetCategory()
    {
        var now = _at.AddSeconds(10);
        var calc = new ScoreCalculator();
        IReadOnlyCollection<Planet> planets = [BuildRichPlanet()];
        IReadOnlyCollection<Fleet> fleets = [BuildRichFleet()];

        // Component-level assertions: tombstones excluded, ships totalled with no double-count, pools
        // read at `now`.
        var components = calc.Extract(planets, fleets, now);
        Assert.Equal(1, components.PlanetCount);
        Assert.Equal(2, components.BuildingCounts[BuildingType.Drill]);      // Operational + Demolishing (not Cancelled)
        Assert.Equal(1, components.BuildingCounts[BuildingType.Refinery]);   // UnderConstruction (not Demolished)
        Assert.Equal(1, components.BuildingCounts[BuildingType.Generator]);
        Assert.Equal(1, components.BuildingCounts[BuildingType.Shipyard]);
        Assert.Equal(5, components.BuildingCounts.Values.Sum());             // 7 slots present, 2 tombstones dropped
        Assert.Equal(3, components.ShipCounts[ShipType.ColonyShip]);         // 2 queue + 1 fleet
        Assert.Equal(3, components.ShipCounts[ShipType.CargoVessel]);        // 1 roster + 1 queue + 1 fleet
        Assert.Equal(150m + 20m, components.IronOre);                        // pool@now + cargo
        Assert.Equal(60m + 10m, components.IronIngot);

        var expected =
            (1 * ScoringSpecs.PointsPerPlanet)
            + (2 * ScoringSpecs.BuildingPoints(BuildingType.Drill))
            + ScoringSpecs.BuildingPoints(BuildingType.Refinery)
            + ScoringSpecs.BuildingPoints(BuildingType.Generator)
            + ScoringSpecs.BuildingPoints(BuildingType.Shipyard)
            + (3 * ScoringSpecs.ShipPoints(ShipType.ColonyShip))
            + (3 * ScoringSpecs.ShipPoints(ShipType.CargoVessel))
            + (170m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronOre))
            + (70m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronIngot));

        Assert.Equal(expected, calc.Score(components));
        Assert.Equal(expected, calc.Compute(planets, fleets, now));
    }

    // The no-double-count backstop: the SAME ship id present on both a planet roster and a fleet is
    // counted exactly once (verifying the HashSet dedup in CountShips).
    [Fact]
    public void ExtractDeduplicatesAShipThatAppearsInBothRosterAndFleet()
    {
        var sharedId = Guid.NewGuid();
        var planet = NewOwnedPlanet();
        planet.Ships = [new RosterShip(sharedId, ShipType.CargoVessel, _at, _owner)];
        var fleet = NewStationedFleet(new FleetShip(sharedId, ShipType.CargoVessel, _at));

        var components = new ScoreCalculator().Extract([planet], [fleet], _at);

        Assert.Equal(1, components.ShipCounts[ShipType.CargoVessel]);
    }

    // Resources must be read at `now`, never from the stored checkpoint value.
    [Fact]
    public void ExtractEvaluatesResourcePoolsAtNowNotAtCheckpoint()
    {
        var planet = NewOwnedPlanet();
        planet.IronOre = new ResourcePool(100m, 5m, 10000m, _at);   // 100 at _at, +5/s
        planet.IronIngot = new ResourcePool(0m, 0m, 5000m, _at);
        var now = _at.AddSeconds(20);

        var components = new ScoreCalculator().Extract([planet], [], now);

        Assert.Equal(200m, components.IronOre);                          // 100 + 5*20, evaluated at now
        Assert.NotEqual(planet.IronOre.CheckpointValue, components.IronOre); // not the stale checkpoint
    }

    // A Disbanded fleet is a tombstone: its ships and cargo must not contribute.
    [Fact]
    public void ExtractIgnoresDisbandedFleets()
    {
        var fleet = NewStationedFleet(new FleetShip(Guid.NewGuid(), ShipType.ColonyShip, _at));
        fleet.Status = FleetStatus.Disbanded;

        var components = new ScoreCalculator().Extract([], [fleet], _at);

        Assert.Empty(components.ShipCounts);
        Assert.Equal(0m, components.IronOre);
        Assert.Equal(0m, components.IronIngot);
    }

    // Direct Score(ScoreComponents) test with hand-built components — the pure seam #68 reuses.
    [Fact]
    public void ScoreAppliesSpecsToHandBuiltComponents()
    {
        var components = new ScoreComponents(
            PlanetCount: 2,
            BuildingCounts: new Dictionary<BuildingType, int>
            {
                [BuildingType.Shipyard] = 1,
                [BuildingType.Drill] = 3,
            },
            ShipCounts: new Dictionary<ShipType, int> { [ShipType.ColonyShip] = 1 },
            IronOre: 50m,
            IronIngot: 10m);

        var expected =
            (2 * ScoringSpecs.PointsPerPlanet)
            + ScoringSpecs.BuildingPoints(BuildingType.Shipyard)
            + (3 * ScoringSpecs.BuildingPoints(BuildingType.Drill))
            + ScoringSpecs.ShipPoints(ShipType.ColonyShip)
            + (50m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronOre))
            + (10m * ScoringSpecs.ResourcePointsPerUnit(ResourceType.IronIngot));

        Assert.Equal(expected, new ScoreCalculator().Score(components));
    }
}
