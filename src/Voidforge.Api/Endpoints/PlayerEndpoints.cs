using System.Security.Claims;
using System.Security.Cryptography;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Voidforge.Api.Auth;
using Voidforge.Api.Documents;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Http;
using Voidforge.Api.WorldGeneration;
using Wolverine;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class PlayerEndpoints
{
    // Bounded re-pick attempts for the guarded homeworld claim below (D10/#19). Three attempts
    // gives ample headroom against genuine ties at MVP concurrency levels without looping
    // indefinitely on a pathologically contested world.
    private const int _maxClaimAttempts = 3;

    [AllowAnonymous]
    [WolverinePost("/api/players/register")]
    public static async Task<Results<Ok<RegisterPlayerResponse>, Conflict<string>, StatusCodeHttpResult>> Register(
        RegisterPlayerRequest request,
        IDocumentStore store,
        IMessageBus bus,
        IOptions<WorldGenOptions> worldGenOptions,
        TimeProvider timeProvider)
    {
        // Name-taken check happens once, up front, on its own session — it doesn't participate
        // in the per-attempt claim/retry below.
        await using (var nameCheckSession = store.LightweightSession())
        {
            var nameTaken = await nameCheckSession.Query<Player>()
                .AnyAsync(p => p.Name == request.Name);

            if (nameTaken)
            {
                return TypedResults.Conflict("Player name is already taken.");
            }
        }

        var playerId = Guid.NewGuid();
        var rawKey = GenerateApiKey();
        var hashedKey = ApiKeyAuthenticationHandler.HashKey(rawKey);
        var opts = worldGenOptions.Value;
        var now = timeProvider.GetUtcNow();

        // Guarded claim (D10/#19): the SAME shape as the fleet Colonize claim in
        // CompleteFleetArrivalHandler/Planet.Claim — FetchForWriting, a null-owner check before
        // appending, and optimistic concurrency on SaveChangesAsync catching a genuine tie
        // between two racers (another registration, or a fleet's Colonize arrival) that both
        // saw OwnerId null. Registration does NOT route through Planet.Claim itself: that
        // factory claims bare (zero starting stores) for the fleet path, whereas registration
        // seeds its own starting stores/buildings — the claim GUARD is shared, the claim
        // FACTORY/payload is not. A failed SaveChangesAsync leaves a Marten session's pending
        // unit of work unusable (it can't be selectively unwound), so each attempt opens a
        // fresh session (TryClaimHomeworld below) — nothing from a losing attempt (Player
        // stream, ApiKey, stale Planet append) carries over to the next.
        for (var attempt = 0; attempt < _maxClaimAttempts; attempt++)
        {
            var (outcome, response) = await TryClaimHomeworld(store, bus, playerId, rawKey, hashedKey, opts, request.Name, now);
            switch (outcome)
            {
                case ClaimOutcome.Claimed:
                    return TypedResults.Ok(response!);
                case ClaimOutcome.NameTaken:
                    // A concurrent racer committed this Player.Name between our up-front pre-check
                    // and our commit, tripping the Player.Name unique index. Return immediately —
                    // this is NOT the planet re-pick path: retrying would re-hit the same duplicate
                    // name every attempt and exhaust the loop pointlessly.
                    return TypedResults.Conflict("Player name is already taken.");
                case ClaimOutcome.NoUncolonizedPlanets:
                    return TypedResults.StatusCode(503);
                case ClaimOutcome.LostRace:
                    continue;   // stale read or a genuine tie lost on commit — re-pick
            }
        }

        return TypedResults.StatusCode(503);
    }

    private enum ClaimOutcome
    {
        Claimed,
        LostRace,
        NameTaken,
        NoUncolonizedPlanets,
    }

    // One claim attempt on its own fresh session (see the Register comment on why per-attempt
    // sessions are required). Mirrors the fleet Colonize claim's guard shape: FetchForWriting,
    // a null-owner check before appending, optimistic concurrency on SaveChangesAsync.
    private static async Task<(ClaimOutcome Outcome, RegisterPlayerResponse? Response)> TryClaimHomeworld(
        IDocumentStore store, IMessageBus bus, Guid playerId, string rawKey, string hashedKey, WorldGenOptions opts, string playerName, DateTimeOffset now)
    {
        await using var session = store.LightweightSession();

        // Perf: loads all uncolonized planet IDs into memory. Replace with COUNT + random
        // offset or database-side random selection when planet counts grow large.
        var uncolonized = await session.Query<Planet>()
            .Where(p => p.OwnerId == null)
            .Select(p => p.Id)
            .ToListAsync();

        if (uncolonized.Count == 0)
        {
            return (ClaimOutcome.NoUncolonizedPlanets, null);
        }

        var homeworldId = uncolonized[Random.Shared.Next(uncolonized.Count)];

        var stream = await session.Events.FetchForWriting<Planet>(homeworldId);
        if (stream.Aggregate?.OwnerId is not null)
        {
            // Stale read: another registration or a fleet's Colonize claim took this planet between
            // our query and FetchForWriting (no exception) — the caller re-picks on the next attempt.
            return (ClaimOutcome.LostRace, null);
        }

        session.Events.StartStream<Player>(playerId, new PlayerRegistered(playerName, now));
        stream.AppendMany(
            new PlanetColonized(playerId, opts.StartingIronOre, opts.StartingIronIngots, now),
            // Starting buildings: 1 Drill (ore extraction), 1 Refinery (ore->ingots),
            // 1 Generator (energy). Placed directly as Operational at the colonization instant.
            new BuildingPlaced(BuildingType.Drill, now),
            new BuildingPlaced(BuildingType.Refinery, now),
            new BuildingPlaced(BuildingType.Generator, now));
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            HashedKey = hashedKey,
            PlayerId = playerId,
        });

        try
        {
            await session.SaveChangesAsync();
            await ScheduleInitialStorageChecks(session, bus, homeworldId, now);
            return (ClaimOutcome.Claimed, new RegisterPlayerResponse(playerId, rawKey, homeworldId));
        }
        catch (ConcurrencyException)
        {
            // Lost a genuine tie on this planet's stream version — the caller re-picks on the
            // next attempt. Nothing committed: this attempt's own session discards the queued
            // Player/ApiKey writes along with the failed Planet append.
            return (ClaimOutcome.LostRace, null);
        }
        catch (Exception ex) when (MartenExceptions.IsUniqueViolation(ex))
        {
            // Lost the NAME race: a concurrent registration committed this Player.Name between our
            // pre-check and this commit, tripping the Player.Name unique index (Marten wraps it as
            // Npgsql PostgresException 23505). Register returns 409 now, NOT the LostRace re-pick.
            return (ClaimOutcome.NameTaken, null);
        }
    }

    // Schedule the freshly-seeded homeworld's initial cascade checks (#69/#70), mirroring the other
    // mutation sites (BuildingEndpoints.Place etc.). The homeworld is seeded with an Operational Drill
    // (net ore inflow) draining a finite deposit, so this arms both the storage-full check AND the
    // deposit-depletion check from the outset. Without an initial check the Drill would just clamp and keep drawing full
    // energy until the player's next building/ship action reschedules. Scheduled via the bus rather
    // than the committed transaction (this is a bare store session, not the endpoint's outbox-enrolled
    // one), so a lost/duplicate initial check is self-healing: validate-on-arrival no-ops and any later
    // mutation reschedules. FetchLatest reads the just-committed inline snapshot for post-seed rates.
    private static async Task ScheduleInitialStorageChecks(
        IDocumentSession session, IMessageBus bus, Guid homeworldId, DateTimeOffset now)
    {
        var seeded = await session.Events.FetchLatest<Planet>(homeworldId);
        if (seeded is not null)
        {
            await StorageHaltScheduling.ScheduleAllChecksAsync(bus, homeworldId, seeded, now);
        }
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
