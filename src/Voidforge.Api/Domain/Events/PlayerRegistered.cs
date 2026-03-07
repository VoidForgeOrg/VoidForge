namespace Voidforge.Api.Domain.Events;

public sealed record PlayerRegistered(string Name, DateTimeOffset RegisteredAt);
