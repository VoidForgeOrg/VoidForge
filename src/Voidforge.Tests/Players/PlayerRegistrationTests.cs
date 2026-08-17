using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Players;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class PlayerRegistrationTests
{
    private readonly IAlbaHost _host;

    public PlayerRegistrationTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task RegisterReturnsPlayerIdAndApiKey()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Player_{Guid.NewGuid():N}")).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        Assert.NotEqual(Guid.Empty, response.PlayerId);
        Assert.StartsWith("vf_", response.ApiKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredApiKeyAuthenticatesSuccessfully()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Player_{Guid.NewGuid():N}")).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);

        await _host.Scenario(s =>
        {
            s.Get.Url("/api/ping");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, response.ApiKey);
            s.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task MeReturnsPlayerInfo()
    {
        var name = $"Player_{Guid.NewGuid():N}";

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var registration = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(registration);

        var meResult = await _host.Scenario(s =>
        {
            s.Get.Url("/api/players/me");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var me = await meResult.ReadAsJsonAsync<PlayerInfoResponse>();
        Assert.NotNull(me);
        Assert.Equal(registration.PlayerId, me.Id);
        Assert.Equal(name, me.Name);
    }

    [Fact]
    public async Task MeReportsScoreReflectingTheSeededHomeworld()
    {
        var registration = await _host.RegisterPlayer("Score_");

        var me = await _host.GetJson<PlayerInfoResponse>(registration, "/api/players/me");

        // Seeded homeworld = 1 planet + an Operational Drill + Refinery + Generator (PlayerEndpoints
        // seeds all three at colonization). Assert the planet+buildings FLOOR from ScoringSpecs, not an
        // exact value: the producing Drill accrues ore between the seed and this read, so resources can
        // only push the score ABOVE the floor — exact-equality would be brittle. The exact-value proof
        // lives in ScoreCalculatorTests (fixed pools, no live accrual).
        var planetAndBuildingsFloor =
            ScoringSpecs.PointsPerPlanet
            + ScoringSpecs.BuildingPoints(BuildingType.Drill)
            + ScoringSpecs.BuildingPoints(BuildingType.Refinery)
            + ScoringSpecs.BuildingPoints(BuildingType.Generator);

        Assert.True(me.Score > 0m, $"Score should be positive, was {me.Score}.");
        Assert.True(
            me.Score >= planetAndBuildingsFloor,
            $"Score {me.Score} should be at least the planet+buildings floor {planetAndBuildingsFloor}.");
    }

    [Fact]
    public async Task MeWithoutAuthReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/api/players/me");
            s.StatusCodeShouldBe(401);
        });
    }

    [Fact]
    public async Task RegisterCreatesPlayerAggregate()
    {
        var name = $"Player_{Guid.NewGuid():N}";

        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var registration = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(registration);

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var player = await session.LoadAsync<Player>(registration.PlayerId);
        Assert.NotNull(player);
        Assert.Equal(name, player.Name);
    }

    [Fact]
    public async Task RegisterDuplicateNameReturns409()
    {
        var name = $"Taken_{Guid.NewGuid():N}";

        await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task RegisterAssignsHomeworldWithStartingResources()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Player_{Guid.NewGuid():N}")).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        Assert.NotEqual(Guid.Empty, response.HomeworldId);

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var planet = await session.LoadAsync<Planet>(response.HomeworldId);
        Assert.NotNull(planet);
        Assert.Equal(response.PlayerId, planet.OwnerId);
        Assert.True(planet.IronOre.CheckpointValue > 0);
        Assert.True(planet.IronIngot.CheckpointValue > 0);
    }

    [Fact]
    public async Task TwoPlayersGetDifferentHomeworlds()
    {
        var result1 = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Player_{Guid.NewGuid():N}")).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var result2 = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"Player_{Guid.NewGuid():N}")).ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response1 = await result1.ReadAsJsonAsync<RegisterPlayerResponse>();
        var response2 = await result2.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response1);
        Assert.NotNull(response2);
        Assert.NotEqual(response1.HomeworldId, response2.HomeworldId);
    }

    [Fact]
    public async Task ConcurrentSameNameRegistrationsYieldOneWinnerAndConflicts()
    {
        var name = $"Race_{Guid.NewGuid():N}";

        async Task<int> RegisterOnce()
        {
            var result = await _host.Scenario(s =>
            {
                s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
                s.IgnoreStatusCode();
            });
            return result.Context.Response.StatusCode;
        }

        // Fire N same-name registrations at once: the winner commits the Player.Name first, the
        // rest trip the unique index and must come back as 409 (never a 500 that escapes).
        var codes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RegisterOnce()));

        Assert.Equal(1, codes.Count(c => c == 200));
        Assert.Equal(7, codes.Count(c => c == 409));
        Assert.DoesNotContain(codes, c => c == 500);
    }
}
