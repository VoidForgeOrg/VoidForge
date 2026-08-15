namespace Voidforge.Api.Domain;

public enum BuildingStatus
{
    Operational,
    UnderConstruction,

    // A completed producer whose output storage pool is full (Phase 5, #69): it stops producing
    // and leaves the Operational set, drawing only BuildingSpecs.HaltedDrawFactor of its rating.
    Halted,

    // Terminal TOMBSTONE (#72): construction was cancelled. The slot keeps its list position so
    // SlotIndex stays a stable monotonic identifier, but LiveBuildingCount frees the slot. Being
    // none of Operational/UnderConstruction/Halted, it draws/produces NOTHING automatically —
    // CRITICAL: it must NEVER be treated as Halted, or the 5% HaltedDrawFactor branch in
    // Planet.Energy.cs would make a tombstone draw energy.
    Cancelled,

    // Mid-teardown (#72): demolition has started but not finished. Occupies a slot (still counts in
    // LiveBuildingCount) yet draws/produces NOTHING — like the tombstones, it is none of
    // Operational/UnderConstruction/Halted, so it must NEVER equal Halted (no 5% floor).
    Demolishing,

    // Terminal TOMBSTONE (#72): demolition completed. Same semantics as Cancelled — occupies a list
    // position to keep SlotIndex stable, frees the slot via LiveBuildingCount, draws/produces
    // NOTHING, and must NEVER be treated as Halted.
    Demolished,
}
