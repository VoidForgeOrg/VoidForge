namespace Voidforge.Api.Domain;

// Why a building is Halted (#69). Only OutputStorageFull is triggered in #69; InputStarved and
// ResourceDepleted are defined here but wired up in #70 (input starvation / depletion).
public enum HaltReason
{
    OutputStorageFull,
    InputStarved,
    ResourceDepleted,
}
