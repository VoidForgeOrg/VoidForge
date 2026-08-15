namespace Voidforge.Api.Domain;

// Disbanded is terminal: the snapshot survives as history and list endpoints filter it
// out unless explicitly requested. InTransit is wired by travel (#49).
public enum FleetStatus
{
    Stationed,
    InTransit,
    Disbanded,
}
