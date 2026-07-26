using Marten;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Endpoints;

public static class CompleteFleetArrivalHandler
{
    public static async Task Handle(CompleteFleetArrival message, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Fleet>(message.FleetId);
        var fleet = stream.Aggregate;
        if (fleet is null)
        {
            return;
        }

        // Capture pre-arrival state now: AppendMany below only queues events for the next
        // SaveChangesAsync, it does not re-apply them to this in-memory aggregate, but
        // Arrive() (a pure function of Status/ArrivesAt) is about to logically retire the
        // transit block, so read mission/destination/cargo off the aggregate before that
        // happens rather than relying on state that's about to be conceptually stale.
        var mission = fleet.Mission;
        var destinationId = fleet.DestinationPlanetId;
        var cargoOre = fleet.CargoIronOre;
        var cargoIngot = fleet.CargoIronIngot;

        var events = fleet.Arrive(message.ArrivesAt);
        if (events.Count == 0)
        {
            return;   // stale or superseded message (ADR 0001 validate-on-arrival)
        }

        stream.AppendMany([.. events]);

        // Mission-specific arrival effects (both cross-aggregate, both committed under the
        // one SaveChangesAsync below): Transport delivers cargo (spec §2.4, the codebase's
        // first cross-aggregate append); Colonize (#51) attempts a guarded claim. Each helper
        // returns the Fleet-stream events it produced for Handle to append here — the
        // Planet-stream side effects are appended by the helper itself, on the same session.
        if (mission == MissionType.Transport && destinationId is not null && (cargoOre > 0 || cargoIngot > 0))
        {
            var unloaded = await HandleTransportArrival(session, fleet, destinationId.Value, cargoOre, cargoIngot, message.ArrivesAt);
            if (unloaded is not null)
            {
                stream.AppendOne(unloaded);
            }
        }
        else if (mission == MissionType.Colonize && destinationId is not null)
        {
            var colonizeEvents = await HandleColonizeArrival(session, fleet, destinationId.Value, cargoOre, cargoIngot, message.ArrivesAt);
            stream.AppendMany(colonizeEvents);
        }

        await session.SaveChangesAsync();   // ONE commit across both streams
    }

    // Transport delivers cargo on arrival (spec §2.4). Returns the Fleet-side CargoUnloaded
    // event to append, or null when there's nothing to deliver (destination gone or no
    // longer same-owner — see the re-check note below).
    private static async Task<object?> HandleTransportArrival(
        IDocumentSession session, Fleet fleet, Guid destinationId, decimal cargoOre, decimal cargoIngot, DateTimeOffset at)
    {
        var planetStream = await session.Events.FetchForWriting<Planet>(destinationId);
        var planet = planetStream.Aggregate;
        // Re-check on arrival (spec §2.4): cannot fail in MVP (planet ownership never
        // changes hands — no combat yet), but arrival is the honest place for the
        // invariant; post-MVP combat makes this check live.
        if (planet is null || planet.OwnerId != fleet.OwnerId)
        {
            return null;
        }

        var delivered = planet.AcceptCargoDelivery(fleet.Id, cargoOre, cargoIngot, at);
        planetStream.AppendOne(delivered);
        // Accepting 0 (destination storage full) is a legitimate outcome — cargo
        // simply rides along Stationed rather than being lost.
        return fleet.UnloadCargo(destinationId, delivered.IronOre, delivered.IronIngot, at);
    }

    // Colonize's guarded claim on arrival (#51, spec §2.4). Returns the Fleet-side events to
    // append: the guarded-claim happy path (ColonyShipConsumed + optional CargoUnloaded) or,
    // on the failure branch, ColonizationFailed alone.
    private static async Task<IReadOnlyList<object>> HandleColonizeArrival(
        IDocumentSession session, Fleet fleet, Guid destinationId, decimal cargoOre, decimal cargoIngot, DateTimeOffset at)
    {
        var planetStream = await session.Events.FetchForWriting<Planet>(destinationId);
        var planet = planetStream.Aggregate;
        if (planet is not null && planet.OwnerId is null)
        {
            planetStream.AppendOne(planet.Claim(fleet.OwnerId, at));
            var fleetEvents = new List<object> { fleet.ConsumeColonyShip(destinationId, at) };
            if (cargoOre > 0 || cargoIngot > 0)
            {
                // Headroom computes against the PRE-colonization in-memory pool (AppendOne does not
                // re-apply): benign — an uncolonized pool is zero-value/zero-rate, so headroom is the
                // full capacity, which is exactly the post-claim truth (zero starting stores).
                var delivered = planet.AcceptCargoDelivery(fleet.Id, cargoOre, cargoIngot, at);
                planetStream.AppendOne(delivered);
                fleetEvents.Add(fleet.UnloadCargo(destinationId, delivered.IronOre, delivered.IronIngot, at));
            }

            return fleetEvents;
        }

        // Lost the race (or targeted an owned world): ship preserved, cargo intact, fleet idles
        // here Stationed. A true tie loses on commit with ConcurrencyException, is retried whole
        // by the #39 policy, re-reads the now-owned planet, and lands in this branch (D10).
        return [fleet.RecordColonizationFailure(destinationId, at)];
    }
}
