namespace Voidforge.Api.Domain.Events;

public sealed record ColonizationFailed(Guid PlanetId, DateTimeOffset At);
