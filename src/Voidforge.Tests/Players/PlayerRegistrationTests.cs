using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Players;

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
}
