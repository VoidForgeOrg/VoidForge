using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Wolverine;
using Xunit;

namespace Voidforge.Tests.Colonize;

// #51 (phase-completion e2e, spec §7 item 5): the full loop the epic promises, on the
// REAL Wolverine scheduler (no handler-invoked shortcuts — see ClaimRaceTests/ColonizeMissionTests
// for those), stitched together in one flight: economy (homeworld already producing) -> ships
// (Shipyard, one Colony Ship + one Cargo Vessel) -> expand (Colonize an uncolonized planet in
// ANOTHER solar system, real scheduled arrival, colony owned + zero-store colony receives the
// exact loaded cargo + Colony Ship consumed) -> disband the surviving Cargo Vessel at the new
// colony -> supply the colony (build a SECOND Cargo Vessel at home, launch Transport to the
// colony -- an OWNED destination now, so this is the first true real-scheduler Transport e2e in
// the suite; TransportMissionEndToEndTests's Move round trip predates #51 and could only reach a
// FOREIGN planet) -> real scheduled arrival -> delivered, colony stores incremented exactly.
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class FullLoopEndToEndTests
{
    // Cargo amounts for each leg — small enough for a single Cargo Vessel's capacity (500,
    // Balance__CargoVessel), non-zero enough for the conservation assertions to mean something.
    // Kept identical to ClaimRaceTests'/ColonizeMissionTests' proven-safe values so the two
    // WaitForStock(150, 100) calls below reuse an already-validated recovery margin.
    private const decimal _colonizeCargoIronOre = 100m;
    private const decimal _colonizeCargoIronIngot = 50m;
    private const decimal _transportCargoIronOre = 100m;
    private const decimal _transportCargoIronIngot = 50m;

    private static readonly TimeSpan _arrivalTimeout = TestTimeouts.FullLoopArrival;

    // Ore carried off-planet in the capstone's resume leg. Deliberately LARGE (of the CargoVessel's
    // 500 cap): the assemble endpoint reschedules a CheckStorageFull for when the freed pool would
    // refill to capacity, so a tiny load would re-halt the Drill within ~2s. 400 leaves ~80s of
    // headroom (400 / 5 net-ore-per-second) — well past this test's wall-clock — so the resumed Drill
    // stays Operational through to the final read-API assertion.
    private const decimal _capstoneOreCarriedOffPlanet = 400m;

    private readonly IAlbaHost _host;

    public FullLoopEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task EconomyToShipsToExpansionToSupplyingTheColonyCompletesTheFullLoop()
    {
        var settler = await _host.RegisterPlayer("FullLoopE2E_");
        var homeworld = await _host.GetPlanetById(settler, settler.HomeworldId);

        var destinationId = await ColonizeAnUncolonizedPlanetInAnotherSystem(settler, homeworld.SolarSystemId);
        await SupplyTheColonyViaTransport(settler, destinationId);
    }

    // Ships (Shipyard, one Colony Ship + one Cargo Vessel) -> expand: Colonize an uncolonized
    // planet in ANOTHER solar system on the real scheduler -> colony owned, zero-store colony
    // received the exact loaded cargo, Colony Ship consumed -> disband the surviving Cargo
    // Vessel at the new colony. Returns the colonized planet's id for the Transport leg.
    private async Task<Guid> ColonizeAnUncolonizedPlanetInAnotherSystem(RegisterPlayerResponse settler, Guid homeSystemId)
    {
        var colonyShipId = await _host.BuildRosterShip(settler, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(settler, ShipType.CargoVessel);
        await _host.WaitForStock(settler, 150m, 100m);

        var colonizeFleet = await _host.AssembleFleet(
            settler, [colonyShipId, cargoVesselId], new CargoRequest(_colonizeCargoIronOre, _colonizeCargoIronIngot));
        var destinationId = await _host.FindUncolonizedPlanet(settler, excludeSystemId: homeSystemId);

        var launchedColonize = await _host.Launch(settler, colonizeFleet.Id, MissionType.Colonize, destinationId);
        Assert.Equal(FleetStatus.InTransit, launchedColonize.Status);
        Assert.Equal(destinationId, launchedColonize.DestinationPlanetId);
        Assert.Equal(MissionType.Colonize, launchedColonize.Mission);

        var arrivedColonize = await _host.PollFleetUntil(
            settler,
            colonizeFleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrivedColonize.Status);
        Assert.Equal(destinationId, arrivedColonize.LocationPlanetId);
        var survivingShip = Assert.Single(arrivedColonize.Ships);
        Assert.Equal(ShipType.CargoVessel, survivingShip.Type);   // the Colony Ship was consumed
        Assert.Equal(0m, arrivedColonize.CargoIronOre);           // auto-unloaded into the colony
        Assert.Equal(0m, arrivedColonize.CargoIronIngot);

        var colony = await _host.GetPlanetById(settler, destinationId);
        Assert.Equal(settler.PlayerId, colony.OwnerId);
        // Planet.Claim seeds zero stores AND zero production rate (a fresh colony has no
        // buildings), so the exact-equality assertion is safe: nothing accrues between the
        // claim and this read.
        Assert.Equal(_colonizeCargoIronOre, colony.IronOre.CurrentValue);
        Assert.Equal(_colonizeCargoIronIngot, colony.IronIngot.CurrentValue);
        Assert.Equal(0m, colony.IronOre.Rate);
        Assert.Equal(0m, colony.IronIngot.Rate);

        // Disband the surviving Cargo Vessel at the new colony (cargo is already empty, so
        // D11's cargo-aboard guard doesn't block it).
        var disbanded = await _host.Disband(settler, colonizeFleet.Id);
        Assert.Equal(FleetStatus.Disbanded, disbanded.Status);

        return destinationId;
    }

    // Supply the colony: a second Cargo Vessel built at home, Transport to the now-OWNED colony
    // on the real scheduler -- the first true real-scheduler Transport-to-own-planet round trip
    // in the suite (TransportMissionEndToEndTests predates #51 and could only reach a foreign
    // planet via Move). Still a zero-production colony, so exact-equality delivery math holds.
    private async Task SupplyTheColonyViaTransport(RegisterPlayerResponse settler, Guid destinationId)
    {
        var secondCargoVesselId = await _host.BuildRosterShip(settler, ShipType.CargoVessel);
        await _host.WaitForStock(settler, 150m, 100m);

        var transportFleet = await _host.AssembleFleet(
            settler, [secondCargoVesselId], new CargoRequest(_transportCargoIronOre, _transportCargoIronIngot));

        var launchedTransport = await _host.Launch(settler, transportFleet.Id, MissionType.Transport, destinationId);
        Assert.Equal(FleetStatus.InTransit, launchedTransport.Status);
        Assert.Equal(destinationId, launchedTransport.DestinationPlanetId);
        Assert.Equal(MissionType.Transport, launchedTransport.Mission);

        var arrivedTransport = await _host.PollFleetUntil(
            settler,
            transportFleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationId,
            _arrivalTimeout);

        Assert.Equal(FleetStatus.Stationed, arrivedTransport.Status);
        Assert.Equal(destinationId, arrivedTransport.LocationPlanetId);
        Assert.Equal(0m, arrivedTransport.CargoIronOre);      // auto-delivered on arrival
        Assert.Equal(0m, arrivedTransport.CargoIronIngot);

        var colonyAfterTransport = await _host.GetPlanetById(settler, destinationId);
        Assert.Equal(settler.PlayerId, colonyAfterTransport.OwnerId);
        Assert.Equal(_colonizeCargoIronOre + _transportCargoIronOre, colonyAfterTransport.IronOre.CurrentValue);
        Assert.Equal(_colonizeCargoIronIngot + _transportCargoIronIngot, colonyAfterTransport.IronIngot.CurrentValue);
    }

    // #67 (score acceptance): register -> build an economy -> construct ships -> colonize, reading
    // GET /api/players/me at three checkpoints and asserting the lazily-computed score REFLECTS the
    // acquired assets.
    //
    // On assertion strategy (deliberate — the exact-value proof lives in ScoreCalculatorTests, which
    // uses fixed pools with no live accrual):
    //   * Every checkpoint asserts a computed LOWER BOUND from ScoringSpecs (planets + buildings +
    //     ships only). Resource contributions are >= 0, so the durable-asset floor is always safe.
    //   * We assert a STRICT increase ONLY for the colonize step (afterShips < afterColonize): it is
    //     genuinely monotonic — between those two reads nothing is constructed (no resource drain),
    //     the consumed Colony Ship (-ShipPoints) is outweighed by the new planet (+PointsPerPlanet),
    //     and the loaded cargo merely MOVES homeworld -> fleet -> colony (all still owned, so the
    //     resource term is conserved and only grows as the homeworld keeps producing).
    //   * We do NOT assert register < afterShips: building the Shipyard + two ships DRAINS ore/ingot
    //     from the homeworld, so the resource term can fall further than the +durable gain rises —
    //     an exact/strict comparison there would be brittle. The lower-bound check covers it instead.
    [Fact]
    public async Task ScoreReflectsAssetsAcquiredAcrossTheFullLoop()
    {
        var settler = await _host.RegisterPlayer("ScoreLoopE2E_");
        var homeworld = await _host.GetPlanet(settler);

        // Checkpoint 1 — freshly seeded homeworld: 1 planet + Operational Drill + Refinery + Generator.
        var registerFloor =
            ScoringSpecs.PointsPerPlanet
            + ScoringSpecs.BuildingPoints(BuildingType.Drill)
            + ScoringSpecs.BuildingPoints(BuildingType.Refinery)
            + ScoringSpecs.BuildingPoints(BuildingType.Generator);
        var scoreAfterRegister = await _host.GetScore(settler);
        Assert.True(
            scoreAfterRegister >= registerFloor,
            $"After register, score {scoreAfterRegister} should be >= planet+buildings floor {registerFloor}.");

        // Build economy: a Shipyard (placed by the first BuildRosterShip) plus a Colony Ship and a
        // Cargo Vessel on the roster.
        var colonyShipId = await _host.BuildRosterShip(settler, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(settler, ShipType.CargoVessel);
        await _host.WaitForStock(settler, 150m, 100m);

        // Checkpoint 2 — floor now includes the Shipyard and both roster ships.
        var shipsFloor =
            registerFloor
            + ScoringSpecs.BuildingPoints(BuildingType.Shipyard)
            + ScoringSpecs.ShipPoints(ShipType.ColonyShip)
            + ScoringSpecs.ShipPoints(ShipType.CargoVessel);
        var scoreAfterShips = await _host.GetScore(settler);
        Assert.True(
            scoreAfterShips >= shipsFloor,
            $"After building ships, score {scoreAfterShips} should be >= floor {shipsFloor}.");

        // Colonize a planet in ANOTHER system with both ships aboard: the Colony Ship is consumed on
        // the claim, the Cargo Vessel survives in a Stationed fleet at the new colony (NOT disbanded,
        // so it keeps counting), and the loaded cargo is delivered into the (owned) colony.
        var colonizeFleet = await _host.AssembleFleet(
            settler, [colonyShipId, cargoVesselId], new CargoRequest(_colonizeCargoIronOre, _colonizeCargoIronIngot));
        var destinationId = await _host.FindUncolonizedPlanet(settler, excludeSystemId: homeworld.SolarSystemId);
        var arrived = await _host.LaunchAndArriveInstantly(
            settler, colonizeFleet.Id, MissionType.Colonize, destinationId);
        Assert.Equal(FleetStatus.Stationed, arrived.Status);

        // Checkpoint 3 — a SECOND owned planet appears. Strictly greater than checkpoint 2 (see the
        // assertion-strategy note above) and clears the recomputed floor: +1 planet, -Colony Ship.
        var colonizeFloor =
            shipsFloor
            + ScoringSpecs.PointsPerPlanet
            - ScoringSpecs.ShipPoints(ShipType.ColonyShip);
        var scoreAfterColonize = await _host.GetScore(settler);
        Assert.True(
            scoreAfterColonize > scoreAfterShips,
            $"After colonizing, score {scoreAfterColonize} should strictly exceed the pre-colonize score {scoreAfterShips}.");
        Assert.True(
            scoreAfterColonize >= colonizeFloor,
            $"After colonizing, score {scoreAfterColonize} should be >= floor {colonizeFloor}.");
    }

    // #74 Task 4 (D13, capstone): the whole Phase-5 surface stitched into ONE cohesive flight,
    // verified through the READ API — register -> build an economy -> a producer HALTS on
    // storage-full (#69) -> transport ore away -> the producer RESUMES (#69/D6) -> CANCEL a build
    // (#72) -> RECALL a fleet (#73/D10) -> COLONIZE (#51) -> assert final state via GET endpoints.
    // Explicitly NO score assertion (D13).
    //
    // Determinism note (mirrors StorageHaltingTests / FleetRecallTests / ColonizeMissionTests): a
    // 5 net-ore/s Drill would take ~1900s to fill the 10000-cap ore pool by wall clock, so the
    // halt leg is driven by seeding the pool to capacity and invoking CheckStorageFullHandler
    // DIRECTLY at that instant; the recall-return and colonize arrivals are driven by the shared
    // retry helpers (LaunchAndArriveInstantly / CompleteArrivalWithRetry), which invoke
    // CompleteFleetArrivalHandler with a bounded ConcurrencyException retry so they survive the
    // durable scheduler racing the same arrival, rather than real-scheduler wall-clock waits. Everything else — register, place/cancel building, assemble,
    // launch, recall, and all reads — runs through the real HTTP API. The existing full-loop test
    // above already covers the real-scheduler Colonize + Transport arrivals, so this capstone does
    // not duplicate that and uses the fast, deterministic handler-invocation path instead.
    [Fact]
    public async Task CapstoneHaltResumeCancelRecallColonizeVerifiedThroughTheReadApi()
    {
        var settler = await _host.RegisterPlayer("Capstone74_");
        var homeworld = await _host.GetPlanet(settler);
        Assert.Equal(settler.PlayerId, homeworld.OwnerId);

        var (cargoVesselId, colonyShipId) = await BuildEconomy(settler);

        await HaltHomeworldDrillOnStorageFull(settler);
        var supplyFleetId = await TransportOreAwayAndResumeDrill(settler, cargoVesselId);
        var cancelledSlot = await CancelAConstruction(settler);
        await RecallTheSupplyFleet(settler, supplyFleetId);
        var colonyId = await ColonizeAnUncolonizedPlanet(settler, homeworld.SolarSystemId, colonyShipId);

        await AssertFinalStateThroughTheReadApi(settler, supplyFleetId, cancelledSlot, colonyId);
    }

    // Economy: BuildRosterShip places an operational Shipyard via the API on the first call, then
    // completes each ship onto the roster. One CargoVessel (carries ore off-planet for the resume +
    // recall legs) and one ColonyShip (the colonize leg).
    private async Task<(Guid CargoVesselId, Guid ColonyShipId)> BuildEconomy(RegisterPlayerResponse settler)
    {
        var cargoVesselId = await _host.BuildRosterShip(settler, ShipType.CargoVessel);
        var colonyShipId = await _host.BuildRosterShip(settler, ShipType.ColonyShip);
        return (cargoVesselId, colonyShipId);
    }

    // Producer halts on storage-full (#69), made deterministic exactly as StorageHaltingTests do:
    // pin the ore pool to capacity with a single oversized CargoDeliveredToStorage (Apply clamps
    // CheckpointValue + amount into [0, cap]) and drive CheckStorageFullHandler DIRECTLY at that
    // instant. Only this fill/halt leg uses the handler-invocation shortcut — a real-scheduler fill
    // cannot fit the timeout budget.
    private async Task HaltHomeworldDrillOnStorageFull(RegisterPlayerResponse settler)
    {
        var planetId = settler.HomeworldId;
        var before = await _host.GetPlanet(settler);
        var at = DateTimeOffset.UtcNow;
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        await using (var seedSession = store.LightweightSession())
        {
            var seedStream = await seedSession.Events.FetchForWriting<Planet>(planetId);
            seedStream.AppendOne(new CargoDeliveredToStorage(
                Guid.NewGuid(), before.IronOre.StorageCapacity, 0m, at));
            await seedSession.SaveChangesAsync();
        }

        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = store.LightweightSession();
            await CheckStorageFullHandler.Handle(
                new CheckStorageFull(planetId, ResourceType.IronOre, at), session, bus);
        }

        var halted = await _host.PollBuildingUntilHalted(settler, BuildingType.Drill, HaltReason.OutputStorageFull);
        Assert.Equal(BuildingStatus.Halted, halted.Status);
        Assert.Equal(HaltReason.OutputStorageFull, halted.HaltReason);
    }

    // Transport ore off-planet -> the freed output storage resumes the Drill in the SAME assemble
    // commit (D6, #69). The loaded CargoVessel fleet is returned so the recall leg can reuse it.
    private async Task<Guid> TransportOreAwayAndResumeDrill(RegisterPlayerResponse settler, Guid cargoVesselId)
    {
        var fleet = await _host.AssembleFleet(
            settler, [cargoVesselId], new CargoRequest(_capstoneOreCarriedOffPlanet, 0m));
        Assert.Equal(_capstoneOreCarriedOffPlanet, fleet.CargoIronOre);   // ore transported off the planet

        var resumed = await _host.PollBuildingUntilOperational(settler, BuildingType.Drill);
        Assert.Equal(BuildingStatus.Operational, resumed.Status);
        Assert.Null(resumed.HaltReason);

        var afterResume = await _host.GetPlanet(settler);
        Assert.True(
            afterResume.IronOre.Rate > 0m,
            $"Resumed Drill should produce ore again: rate={afterResume.IronOre.Rate}.");
        return fleet.Id;
    }

    // Cancel a build (#72): place a Generator construction via the API and cancel it while it is
    // still UnderConstruction (the 5s test build duration is a comfortable window). The slot becomes
    // a Cancelled tombstone. A Generator (not a Drill) is chosen so the single-Drill halt/resume
    // assertions stay unambiguous. Returns the tombstoned slot index for the final read.
    private async Task<int> CancelAConstruction(RegisterPlayerResponse settler)
    {
        var placed = await _host.PlaceBuilding(settler, BuildingType.Generator);
        var slotIndex = placed.Buildings.Count - 1;
        Assert.Equal(BuildingStatus.UnderConstruction, placed.Buildings[slotIndex].Status);

        await _host.CancelConstruction(settler, slotIndex);

        var afterCancel = await _host.GetPlanet(settler);
        Assert.Equal(BuildingStatus.Cancelled, afterCancel.Buildings[slotIndex].Status);
        return slotIndex;
    }

    // Recall (#73/D10): send the supply fleet outbound on a Move, then recall it — it turns around
    // and heads back to its origin. Launch + recall run through the real HTTP API; the return
    // arrival is driven by the shared CompleteArrivalWithRetry helper at the recall's fresh ArrivesAt
    // (mirrors FleetRecallTests) — it invokes CompleteFleetArrivalHandler with a bounded
    // ConcurrencyException retry, staying correct if the durable scheduler races it on the stream.
    private async Task RecallTheSupplyFleet(RegisterPlayerResponse settler, Guid fleetId)
    {
        var destination = await _host.FindPlanetOtherThan(settler);
        await _host.Launch(settler, fleetId, MissionType.Move, destination);

        var recalled = await _host.Recall(settler, fleetId);
        Assert.Equal(FleetStatus.InTransit, recalled.Status);
        Assert.Equal(settler.HomeworldId, recalled.DestinationPlanetId);   // heading back to origin
        Assert.NotNull(recalled.RecalledAt);
        Assert.NotNull(recalled.ArrivesAt);

        await _host.CompleteArrivalWithRetry(fleetId, recalled.ArrivesAt.Value);
    }

    // Colonize (#51): assemble the ColonyShip and claim an uncolonized planet in ANOTHER system.
    // Launch runs through the real HTTP API; the arrival is driven by the shared
    // LaunchAndArriveInstantly handler-invocation helper. Returns the colonized planet's id.
    private async Task<Guid> ColonizeAnUncolonizedPlanet(
        RegisterPlayerResponse settler, Guid homeSystemId, Guid colonyShipId)
    {
        var colonizeFleet = await _host.AssembleFleet(settler, [colonyShipId]);
        var destinationId = await _host.FindUncolonizedPlanet(settler, excludeSystemId: homeSystemId);

        var arrived = await _host.LaunchAndArriveInstantly(
            settler, colonizeFleet.Id, MissionType.Colonize, destinationId);
        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationId, arrived.LocationPlanetId);
        Assert.DoesNotContain(arrived.Ships, s => s.Id == colonyShipId);   // colony ship consumed on claim
        return destinationId;
    }

    // Final state, read entirely through the public GET API (D13: NO score assertion).
    private async Task AssertFinalStateThroughTheReadApi(
        RegisterPlayerResponse settler, Guid supplyFleetId, int cancelledSlot, Guid colonyId)
    {
        // Homeworld: the Drill resumed (Operational, no halt reason), its output pool sits below cap,
        // and the cancelled construction is a terminal tombstone.
        var homeworld = await _host.GetPlanet(settler);
        var drill = homeworld.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Operational, drill.Status);
        Assert.Null(drill.HaltReason);
        Assert.True(
            homeworld.IronOre.CurrentValue < homeworld.IronOre.StorageCapacity,
            $"Ore should be below cap after the off-planet load: {homeworld.IronOre.CurrentValue} / {homeworld.IronOre.StorageCapacity}.");
        Assert.Equal(BuildingStatus.Cancelled, homeworld.Buildings[cancelledSlot].Status);

        // Supply fleet: recalled home — Stationed at the origin with its carried ore intact.
        var supplyFleet = await _host.GetJson<FleetResponse>(settler, $"/api/fleets/{supplyFleetId}");
        Assert.Equal(FleetStatus.Stationed, supplyFleet.Status);
        Assert.Equal(settler.HomeworldId, supplyFleet.LocationPlanetId);
        Assert.Equal(_capstoneOreCarriedOffPlanet, supplyFleet.CargoIronOre);

        // Colony: owned by the settler after the Colonize claim.
        var colony = await _host.GetPlanetById(settler, colonyId);
        Assert.Equal(settler.PlayerId, colony.OwnerId);
    }
}
