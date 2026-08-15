using Alba;
using Voidforge.Api.Domain;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Fleets;

// Merge-gate e2e test: exercises the full fleet-assembly feature end to end against real
// scheduled ship completions (build -> assemble -> roster shrinks -> disband -> ships returned).
[Collection(IntegrationCollection.Name)]
public sealed class FleetRoundTripTests
{
    private readonly IAlbaHost _host;

    public FleetRoundTripTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ShipsRoundTripThroughAFleet()
    {
        var registration = await _host.RegisterPlayer("FleetRT_");
        await _host.EnsureOperationalShipyard(registration);
        var ship1 = await _host.BuildRosterShip(registration);
        var ship2 = await _host.BuildRosterShip(registration);

        var rosterBefore = await _host.GetRoster(registration);
        Assert.Equal(2, rosterBefore.TotalItems);

        var fleet = await _host.AssembleFleet(registration, [ship1, ship2]);
        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(2, fleet.Ships.Count);
        Assert.Equal(0, (await _host.GetRoster(registration)).TotalItems);  // roster shrank

        await _host.Disband(registration, fleet.Id);

        var rosterAfter = await _host.GetRoster(registration);
        Assert.Equal(2, rosterAfter.TotalItems);                      // ships returned
        Assert.All(rosterAfter.Items, s => Assert.Equal(registration.PlayerId, s.OwnerId));
    }
}
