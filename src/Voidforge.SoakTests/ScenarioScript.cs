using Alba;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;

namespace Voidforge.SoakTests;

// The two-user "economy + own-colony supply line + depletion" scenario, driven over real HTTP with
// ONLY the real scheduler (Launch + PollFleetUntil — never LaunchAndArriveInstantly). Each leg is
// wrapped so an EXPECTED contention outcome (a 4xx surfaced as an Alba assertion, or a polling helper
// timing out) is recorded and the script continues; only a truly unexpected fault propagates.
public static class ScenarioScript
{
    // Modest cargo load: well within two CargoVessels' combined capacity and the homeworld's stored ore.
    private const decimal _transportOre = 100m;

    // Player A (industrialist): grow the economy (a 2nd Drill drives the depletion), colonize a second
    // planet in another system, then run a real ore supply line home -> its OWN colony. Transport to a
    // same-owner destination delivers and auto-unloads (contrast a foreign destination, which 403s).
    public static async Task RunIndustrialistAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Guid excludeSystemId, Deadline deadline)
    {
        await RunLeg(recorder, "A: place 2nd Drill", () => host.PlaceBuilding(reg, BuildingType.Drill), deadline);
        await RunLeg(recorder, "A: place Shipyard", () => host.PlaceBuilding(reg, BuildingType.Shipyard), deadline);
        await RunLeg(recorder, "A: shipyard operational", () => host.EnsureOperationalShipyard(reg), deadline);

        var colonyId = await ColonizeSecondPlanetAsync(host, recorder, reg, excludeSystemId, deadline);
        if (deadline.Reached || colonyId is null)
        {
            return;
        }

        await RunSupplyLineAsync(host, recorder, reg, colonyId.Value, deadline);
    }

    // Player B (colonizer): build a Colony Ship, launch a real-scheduler Colonize at an uncolonized
    // planet, then recall the fleet mid-transit (turning it around back home).
    public static async Task RunColonizerAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Guid excludeSystemId, Deadline deadline)
    {
        await RunLeg(recorder, "B: place Shipyard", () => host.PlaceBuilding(reg, BuildingType.Shipyard), deadline);
        await RunLeg(recorder, "B: shipyard operational", () => host.EnsureOperationalShipyard(reg), deadline);

        var colonyShipId = await TryBuildColonyShipAsync(host, recorder, reg, "B", deadline);
        if (deadline.Reached || colonyShipId is null)
        {
            return;
        }

        await RunColonizeThenRecallLegAsync(host, recorder, reg, colonyShipId.Value, excludeSystemId, deadline);
    }

    // Builds a Colony Ship, colonizes a planet in another system, and — after the real scheduler
    // delivers the arrival — confirms ownership (a colonize race with player B could leave the planet
    // owned by the other player). Returns the owned colony id, or null if the colony was not won.
    private static async Task<Guid?> ColonizeSecondPlanetAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Guid excludeSystemId, Deadline deadline)
    {
        var colonyShipId = await TryBuildColonyShipAsync(host, recorder, reg, "A", deadline);
        if (deadline.Reached || colonyShipId is null)
        {
            return null;
        }

        var fleet = await RunLegForResult(
            recorder, "A: assemble colony fleet", () => host.AssembleFleet(reg, [colonyShipId.Value]), deadline);
        if (deadline.Reached || fleet is null)
        {
            return null;
        }

        var target = await FindColonizeTargetAsync(host, recorder, reg, "A", excludeSystemId, deadline);
        if (target is null)
        {
            return null;
        }

        var launched = await RunLegForResult(
            recorder, "A: launch colonize",
            () => host.Launch(reg, fleet.Id, MissionType.Colonize, target.Value), deadline);
        if (launched is null)
        {
            return null;
        }

        await RunLeg(
            recorder, "A: colony fleet arrives",
            () => host.PollFleetUntil(reg, fleet.Id, f => f.Status == FleetStatus.Stationed, SoakTimeouts.FleetArrival),
            deadline);

        if (!await OwnsPlanetAsync(host, recorder, reg, target.Value))
        {
            recorder.RecordEvent($"A: did not win colony {target.Value}; skipping supply line.");
            return null;
        }

        recorder.RecordEvent($"A: colonized {target.Value}");
        return target;
    }

    // Builds cargo vessels and runs a Transport ore supply line from A's homeworld to its OWN colony.
    private static async Task RunSupplyLineAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Guid colonyId, Deadline deadline)
    {
        var cargoShipIds = await BuildCargoVesselsAsync(host, recorder, reg, deadline);
        if (deadline.Reached || cargoShipIds.Count < 1)
        {
            recorder.RecordEvent("A: no cargo vessel available; skipping supply line.");
            return;
        }

        var shipIds = cargoShipIds.Take(2).ToList();
        var fleet = await RunLegForResult(
            recorder, "A: assemble supply fleet",
            () => host.AssembleFleet(reg, shipIds, new CargoRequest(_transportOre, 0m)), deadline);
        if (deadline.Reached || fleet is null)
        {
            return;
        }

        // Transport to A's OWN colony — a same-owner destination, so a real delivery + auto-unload.
        // Capture the raw status (200 launched; a rare 409 if a scheduled completion races the stream).
        await deadline.PauseAsync();
        var status = await host.PostForStatus(
            reg, $"/api/fleets/{fleet.Id}/missions", new LaunchMissionRequest(MissionType.Transport, colonyId));
        recorder.RecordStatus(status);
        recorder.RecordEvent($"A: supply transport -> {status}");

        await RunLeg(
            recorder, "A: supply fleet settles",
            () => host.PollFleetUntil(reg, fleet.Id, f => f.Status == FleetStatus.Stationed, SoakTimeouts.FleetArrival),
            deadline);
    }

    private static async Task<IReadOnlyList<Guid>> BuildCargoVesselsAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Deadline deadline)
    {
        if (deadline.Reached)
        {
            return [];
        }

        await deadline.PauseAsync();
        try
        {
            var ids = await host.BuildRosterShips(reg, 2);
            recorder.RecordEvent($"A: built {ids.Count} cargo vessels");
            return ids;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Ingot starvation can leave a build ConstructionHalted so the batch never fully completes —
            // a modeled outcome under this depletion theme. Recover whatever DID reach the roster.
            recorder.RecordEvent($"A: cargo-vessel batch incomplete ({ex.Message}); recovering roster.");
            return await AvailableCargoVesselIdsAsync(host, recorder, reg);
        }
    }

    private static async Task<IReadOnlyList<Guid>> AvailableCargoVesselIdsAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg)
    {
        try
        {
            var roster = await host.GetRoster(reg);
            return roster.Items.Where(s => s.Type == ShipType.CargoVessel).Select(s => s.Id).ToList();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"A: roster read failed ({ex.Message}).");
            return [];
        }
    }

    private static async Task<Guid?> TryBuildColonyShipAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, string who, Deadline deadline)
    {
        if (deadline.Reached)
        {
            return null;
        }

        await deadline.PauseAsync();
        try
        {
            var id = await host.BuildRosterShip(reg, ShipType.ColonyShip);
            recorder.RecordEvent($"{who}: colony ship built");
            return id;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"{who}: colony ship did not complete ({ex.Message}).");
            return null;
        }
    }

    private static async Task RunColonizeThenRecallLegAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg,
        Guid colonyShipId, Guid excludeSystemId, Deadline deadline)
    {
        var fleet = await RunLegForResult(
            recorder, "B: assemble colony fleet", () => host.AssembleFleet(reg, [colonyShipId]), deadline);
        if (deadline.Reached || fleet is null)
        {
            return;
        }

        var destinationId = await FindColonizeTargetAsync(host, recorder, reg, "B", excludeSystemId, deadline);
        if (destinationId is null)
        {
            return;
        }

        var launched = await RunLegForResult(
            recorder, "B: launch colonize",
            () => host.Launch(reg, fleet.Id, MissionType.Colonize, destinationId.Value), deadline);
        if (launched is null)
        {
            return;
        }

        // Recall the colony fleet mid-transit — a real-scheduler reschedule. Capture the raw status;
        // an already-arrived fleet yields a modeled 409.
        await deadline.PauseAsync();
        var status = await host.CancelForStatus(reg, fleet.Id);
        recorder.RecordStatus(status);
        recorder.RecordEvent($"B: recall -> {status}");

        await RunLeg(
            recorder, "B: colony fleet settles",
            () => host.PollFleetUntil(reg, fleet.Id, f => f.Status == FleetStatus.Stationed, SoakTimeouts.FleetArrival),
            deadline);
    }

    private static async Task<Guid?> FindColonizeTargetAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, string who, Guid excludeSystemId, Deadline deadline)
    {
        await deadline.PauseAsync();
        try
        {
            return await host.FindUncolonizedPlanet(reg, excludeSystemId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"{who}: no uncolonized planet ({ex.Message}).");
            return null;
        }
    }

    private static async Task<bool> OwnsPlanetAsync(
        IAlbaHost host, SoakRecorder recorder, RegisterPlayerResponse reg, Guid planetId)
    {
        try
        {
            var planet = await host.GetPlanetById(reg, planetId);
            return planet.OwnerId == reg.PlayerId;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"A: ownership check failed ({ex.Message}).");
            return false;
        }
    }

    // Runs a value-less leg (result discarded): pauses, invokes, and records any EXPECTED failure.
    private static async Task RunLeg(SoakRecorder recorder, string name, Func<Task> action, Deadline deadline)
    {
        if (deadline.Reached)
        {
            return;
        }

        await deadline.PauseAsync();
        try
        {
            await action();
            recorder.RecordEvent($"{name}: ok");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"{name}: expected failure ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // Runs a value-returning leg, yielding null on an EXPECTED failure so the caller can skip downstream legs.
    private static async Task<T?> RunLegForResult<T>(
        SoakRecorder recorder, string name, Func<Task<T>> action, Deadline deadline)
        where T : class
    {
        if (deadline.Reached)
        {
            return null;
        }

        await deadline.PauseAsync();
        try
        {
            var result = await action();
            recorder.RecordEvent($"{name}: ok");
            return result;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            recorder.RecordEvent($"{name}: expected failure ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // An UnexpectedStatusException (a modeled non-200 from an asserting helper — 4xx contention such as
    // 403/409/503) or an InvalidOperationException/TimeoutException from a polling helper timing out are
    // EXPECTED contention outcomes under this depletion theme; everything else is a real fault and
    // propagates. Crucially, a 5xx never surfaces as UnexpectedStatusException — the harness raises
    // ServerErrorException, which is NOT matched here, so a server error always fails the soak.
    private static bool IsExpected(Exception ex) =>
        ex is UnexpectedStatusException or InvalidOperationException or TimeoutException;
}
