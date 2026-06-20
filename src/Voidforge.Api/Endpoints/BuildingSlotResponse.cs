using Voidforge.Api.Domain;

namespace Voidforge.Api.Endpoints;

public sealed record BuildingSlotResponse(BuildingType Type, BuildingStatus Status);
