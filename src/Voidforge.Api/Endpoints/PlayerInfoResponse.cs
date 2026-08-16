namespace Voidforge.Api.Endpoints;

// Score (#67) is computed lazily on each read from everything the player currently owns
// (ScoreCalculator) — not a stored field.
public sealed record PlayerInfoResponse(Guid Id, string Name, DateTimeOffset RegisteredAt, decimal Score);
