using Alba;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Travel;

// Merge-gate e2e (#49): the real Wolverine scheduler, not a manually-invoked handler
// (that path is HandlerInvokedArrivalStationsTheFleetAndIsIdempotent in
// FleetMissionEndpointTests). AppFixture overrides both ship speeds to 1000 units/s, so even
// a cross-system trip (world seeded with CoordinateRange 1000 → at most ~3500 units) resolves
// in a few seconds of simulated travel time; the poll timeout below is generous mostly to
// absorb Wolverine's scheduled-message poller latency, not the travel itself.
[Collection(IntegrationCollection.Name)]
public sealed class MoveMissionEndToEndTests
{
    private readonly IAlbaHost _host;

    public MoveMissionEndToEndTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task LaunchedFleetTravelsViaTheRealSchedulerAndArrivesStationedWithItsShipOnTheDestinationRoster()
    {
        var owner = await _host.RegisterPlayer("MoveE2E_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        var homeworld = await _host.GetPlanetById(owner, owner.HomeworldId);
        var destinationPlanetId = await PickPlanetInAnotherSolarSystem(owner, homeworld.SolarSystemId);

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Move, destinationPlanetId);

        // Trivially observable mid-flight: nothing has had time to arrive yet.
        Assert.Equal(FleetStatus.InTransit, launched.Status);
        Assert.Null(launched.LocationPlanetId);
        Assert.Equal(destinationPlanetId, launched.DestinationPlanetId);

        var arrived = await _host.PollFleetUntil(
            owner,
            fleet.Id,
            f => f.Status == FleetStatus.Stationed && f.LocationPlanetId == destinationPlanetId,
            TestTimeouts.Arrival);

        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(destinationPlanetId, arrived.LocationPlanetId);
        Assert.Null(arrived.OriginPlanetId);
        Assert.Null(arrived.DestinationPlanetId);
        Assert.Null(arrived.Mission);
        Assert.Null(arrived.DepartedAt);
        Assert.Null(arrived.ArrivesAt);

        await _host.Disband(owner, fleet.Id);

        var destinationRoster = await _host.GetRoster(owner, destinationPlanetId);
        Assert.Contains(destinationRoster.Items, s => s.Id == shipId);
    }

    // Picks the first planet belonging to a solar system other than the homeworld's — this
    // maximizes travel distance (still only seconds at the fixture's fast test speed) and
    // exercises the coordinate-driven planner across systems, not just within one.
    private async Task<Guid> PickPlanetInAnotherSolarSystem(RegisterPlayerResponse registration, Guid homeSolarSystemId)
    {
        var systems = await _host.GetJson<PagedResponse<SolarSystemResponse>>(
            registration, "/api/solar-systems?pageSize=200");
        var other = systems.Items.FirstOrDefault(s => s.Id != homeSolarSystemId && s.PlanetIds.Count > 0);
        if (other is null)
        {
            throw new InvalidOperationException("No solar system other than the homeworld's was found among the seeded world.");
        }

        return other.PlanetIds[0];
    }
}
