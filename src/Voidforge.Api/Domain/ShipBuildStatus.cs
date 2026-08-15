namespace Voidforge.Api.Domain;

public enum ShipBuildStatus
{
    Queued,
    Active,

    // An active ship build paused because ingots ran dry (#83): the IronIngot buffer emptied and no
    // ingots are being produced. It KEEPS its shipyard bay occupied (OccupiedBayCount) so a queued
    // build does not auto-start into the same starvation, but draws NO energy (ActiveShipBuildCount —
    // the fungible-bay energy math — excludes it) and NO ingot drain (RebaseRates' shipBuildDrain
    // filters Status == Active). DISTINCT from the producer-side BuildingStatus.Halted 5% floor;
    // a rating-less paused build draws nothing. Resumes to Active with a pushed-out CompletesAt.
    Halted,
}
