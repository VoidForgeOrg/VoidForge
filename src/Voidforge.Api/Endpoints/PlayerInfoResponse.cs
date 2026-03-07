namespace Voidforge.Api.Endpoints;

public sealed record PlayerInfoResponse(Guid Id, string Name, DateTimeOffset RegisteredAt);
