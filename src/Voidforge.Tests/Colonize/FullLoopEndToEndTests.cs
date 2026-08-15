using Alba;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
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
}
