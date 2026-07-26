namespace Voidforge.Api.Domain;

// Move relocates; Transport and Colonize dispatch beyond a plain relocation land in #50/#51
// (cargo unload, colonization). Depart/Arrive (#49) are mission-agnostic — the mission just
// rides along on the transit block for those later PRs to read.
public enum MissionType
{
    Move,
    Transport,
    Colonize,
}
