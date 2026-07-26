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

        // Transport delivers cargo on arrival (spec §2.4) — the codebase's first
        // cross-aggregate append. Both streams commit under the one SaveChangesAsync below.
        if (mission == MissionType.Transport && destinationId is not null && (cargoOre > 0 || cargoIngot > 0))
        {
            var planetStream = await session.Events.FetchForWriting<Planet>(destinationId.Value);
            var planet = planetStream.Aggregate;
            // Re-check on arrival (spec §2.4): cannot fail in MVP (planet ownership never
            // changes hands — no combat yet), but arrival is the honest place for the
            // invariant; post-MVP combat makes this check live.
            if (planet is not null && planet.OwnerId == fleet.OwnerId)
            {
                var delivered = planet.AcceptCargoDelivery(fleet.Id, cargoOre, cargoIngot, message.ArrivesAt);
                planetStream.AppendOne(delivered);
                // Accepting 0 (destination storage full) is a legitimate outcome — cargo
                // simply rides along Stationed rather than being lost.
                stream.AppendOne(fleet.UnloadCargo(destinationId.Value, delivered.IronOre, delivered.IronIngot, message.ArrivesAt));
            }
        }

        await session.SaveChangesAsync();   // ONE commit across both streams
    }
}
