namespace Voidforge.Api.Domain;

public enum BuildingStatus
{
    Operational,
    UnderConstruction,

    // A completed producer whose output storage pool is full (Phase 5, #69): it stops producing
    // and leaves the Operational set, drawing only BuildingSpecs.HaltedDrawFactor of its rating.
    Halted,
}
