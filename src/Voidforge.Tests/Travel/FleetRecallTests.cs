using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Travel;

// #73 (D10): POST /api/fleets/{id}/cancel recalls an in-transit fleet — it turns around and
// returns to its origin in exactly the time already elapsed, arriving Stationed with cargo and
// colony ship intact. The recall RESPONSE assertions read the endpoint's own post-commit
// snapshot (deterministic). Arrival is driven by invoking CompleteFleetArrivalHandler directly
// (mirrors LaunchAndArriveInstantly) rather than waiting on the durable scheduler.
[Collection(IntegrationCollection.Name)]
public sealed class FleetRecallTests
{
    private readonly IAlbaHost _host;

    public FleetRecallTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task RecallInTransitFleetTurnsAroundAndReturnArrivalStationsAtOriginWithCargoIntact()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");
        var beta = await _host.RegisterPlayer("FleetRecall_Test_");   // an outbound destination to travel toward
        var colonyShipId = await _host.BuildRosterShip(owner, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [colonyShipId, cargoVesselId], new CargoRequest(100m, 50m));

        await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        // Recall: the endpoint's own post-commit snapshot — turned around, heading back to origin.
        var recalled = await _host.Recall(owner, fleet.Id);
        Assert.Equal(FleetStatus.InTransit, recalled.Status);
        Assert.NotNull(recalled.RecalledAt);
        Assert.Equal(owner.HomeworldId, recalled.DestinationPlanetId);   // heading back to its origin
        Assert.Equal(MissionType.Move, recalled.Mission);               // Move return: no colonize/transport effect
        Assert.Equal(100m, recalled.CargoIronOre);                      // cargo untouched by the turnaround
        Assert.Equal(50m, recalled.CargoIronIngot);
        Assert.NotNull(recalled.ArrivesAt);
        var returnArrivesAt = recalled.ArrivesAt.Value;

        // Complete the return arrival at the fresh (return) ArrivesAt — idempotent if the durable
        // scheduler already fired it (Arrive() no-ops once the fleet is no longer InTransit).
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, returnArrivesAt), session);
        }

        var arrived = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, arrived.Status);
        Assert.Equal(owner.HomeworldId, arrived.LocationPlanetId);   // back home
        Assert.Null(arrived.RecalledAt);                            // cleared on arrival
        Assert.Null(arrived.DestinationPlanetId);
        Assert.Equal(100m, arrived.CargoIronOre);                   // cargo survived the round trip
        Assert.Equal(50m, arrived.CargoIronIngot);
        Assert.Contains(arrived.Ships, s => s.Id == colonyShipId);  // colony ship survived (Move return consumes nothing)
        Assert.Contains(arrived.Ships, s => s.Id == cargoVesselId);
    }

    [Fact]
    public async Task StaleOutboundArrivalAfterRecallIsANoOpAndDoesNotStationAtTheOutboundDestination()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");
        var beta = await _host.RegisterPlayer("FleetRecall_Test_");
        var cargoVesselId = await _host.BuildRosterShip(owner, ShipType.CargoVessel);
        await _host.WaitForStock(owner, 150m, 100m);
        var fleet = await _host.AssembleFleet(owner, [cargoVesselId], new CargoRequest(100m, 50m));

        var launched = await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);
        Assert.NotNull(launched.ArrivesAt);
        var outboundArrivesAt = launched.ArrivesAt.Value;   // captured BEFORE recall — now stale

        var recalled = await _host.Recall(owner, fleet.Id);
        Assert.NotNull(recalled.ArrivesAt);
        var returnArrivesAt = recalled.ArrivesAt.Value;

        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // Complete the return first so the fleet is home and settled.
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, returnArrivesAt), session);
        }

        var afterReturn = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterReturn.Status);
        Assert.Equal(owner.HomeworldId, afterReturn.LocationPlanetId);

        // The originally-scheduled outbound arrival now goes stale (ADR 0001 validate-on-arrival):
        // its ArrivesAt no longer matches, so Arrive() returns nothing — no second arrival, and in
        // particular the fleet must NOT be stationed at the abandoned outbound destination.
        await using (var session = store.LightweightSession())
        {
            await CompleteFleetArrivalHandler.Handle(new CompleteFleetArrival(fleet.Id, outboundArrivesAt), session);
        }

        var afterStale = await _host.GetJson<FleetResponse>(owner, $"/api/fleets/{fleet.Id}");
        Assert.Equal(FleetStatus.Stationed, afterStale.Status);
        Assert.Equal(owner.HomeworldId, afterStale.LocationPlanetId);   // home, never beta
        Assert.NotEqual(beta.HomeworldId, afterStale.LocationPlanetId);
        Assert.Equal(100m, afterStale.CargoIronOre);                    // cargo intact — no double delivery
        Assert.Equal(50m, afterStale.CargoIronIngot);
    }

    [Fact]
    public async Task RecallStationedFleetReturns409()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);

        var status = await _host.CancelForStatus(owner, fleet.Id);

        Assert.Equal(409, status);   // only an in-transit fleet can be recalled
    }

    [Fact]
    public async Task SecondRecallReturns409()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");
        var beta = await _host.RegisterPlayer("FleetRecall_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);
        await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);
        await _host.Recall(owner, fleet.Id);

        // Second recall is a 409 whether the fleet is still returning ("already returning") or
        // the durable scheduler has since landed it ("only an in-transit fleet can be recalled").
        var status = await _host.CancelForStatus(owner, fleet.Id);

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task RecallForeignFleetReturns403()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");
        var beta = await _host.RegisterPlayer("FleetRecall_Test_");
        var intruder = await _host.RegisterPlayer("FleetRecall_Test_");
        var shipId = await _host.BuildRosterShip(owner);
        var fleet = await _host.AssembleFleet(owner, [shipId]);
        await _host.Launch(owner, fleet.Id, MissionType.Move, beta.HomeworldId);

        var status = await _host.CancelForStatus(intruder, fleet.Id);

        Assert.Equal(403, status);   // ownership is checked before status
    }

    [Fact]
    public async Task RecallUnknownFleetReturns404()
    {
        var owner = await _host.RegisterPlayer("FleetRecall_Test_");

        var status = await _host.CancelForStatus(owner, Guid.NewGuid());

        Assert.Equal(404, status);
    }
}
