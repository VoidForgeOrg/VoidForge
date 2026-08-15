using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record LaunchMissionRequest(MissionType Mission, Guid DestinationPlanetId);
