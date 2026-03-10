using System.Security.Claims;
using System.Security.Cryptography;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Auth;
using Voidforge.Api.Documents;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.WorldGeneration;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class PlayerEndpoints
{
    [AllowAnonymous]
    [WolverinePost("/api/players/register")]
    public static async Task<Results<Ok<RegisterPlayerResponse>, Conflict<string>, StatusCodeHttpResult>> Register(
        RegisterPlayerRequest request,
        IDocumentSession session,
        IOptions<WorldGenOptions> worldGenOptions)
    {
        var nameTaken = await session.Query<Player>()
            .AnyAsync(p => p.Name == request.Name);

        if (nameTaken)
        {
            return TypedResults.Conflict("Player name is already taken.");
        }

        // Perf: loads all uncolonized planet IDs into memory. Replace with COUNT + random offset
        // or database-side random selection when planet counts grow large.
        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();

        if (uncolonized.Count == 0)
        {
            return TypedResults.StatusCode(503);
        }

        var homeworldId = uncolonized[Random.Shared.Next(uncolonized.Count)];

        var playerId = Guid.NewGuid();
        var rawKey = GenerateApiKey();
        var hashedKey = ApiKeyAuthenticationHandler.HashKey(rawKey);
        var opts = worldGenOptions.Value;

        session.Events.StartStream<Player>(playerId, new PlayerRegistered(request.Name, DateTimeOffset.UtcNow));
        // Race: two concurrent registrations can colonize the same planet. Fix with optimistic
        // concurrency (expected version) on Append. Tracked in GitHub issue.
        session.Events.Append(homeworldId, new PlanetColonized(playerId, opts.StartingIronOre, opts.StartingIronIngots));
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            HashedKey = hashedKey,
            PlayerId = playerId,
        });

        await session.SaveChangesAsync();

        return TypedResults.Ok(new RegisterPlayerResponse(playerId, rawKey, homeworldId));
    }

    [WolverineGet("/api/players/me")]
    public static async Task<Results<Ok<PlayerInfoResponse>, NotFound>> Me(
        ClaimsPrincipal principal,
        IQuerySession session)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var playerId))
        {
            return TypedResults.NotFound();
        }

        var player = await session.LoadAsync<Player>(playerId);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new PlayerInfoResponse(player.Id, player.Name, player.RegisteredAt));
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "vf_" + Convert.ToHexStringLower(bytes);
    }
}
