using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Documents;
using Xunit;

namespace Voidforge.Tests.Auth;

[Collection(IntegrationCollection.Name)]
public sealed class ApiKeyAuthTests : IAsyncLifetime
{
    private const string _testRawKey = "test-api-key-for-integration";
    private readonly IAlbaHost _host;

    public ApiKeyAuthTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    public async Task InitializeAsync()
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var hashedKey = ApiKeyAuthenticationHandler.HashKey(_testRawKey);

        var existing = await session.Query<ApiKey>()
            .FirstOrDefaultAsync(k => k.HashedKey == hashedKey);

        if (existing is null)
        {
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                HashedKey = hashedKey,
                PlayerId = Guid.NewGuid(),
            });
            await session.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PingWithoutKeyReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/api/ping");
            s.StatusCodeShouldBe(401);
        });
    }

    [Fact]
    public async Task PingWithInvalidKeyReturns401()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/api/ping");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, "bogus-key");
            s.StatusCodeShouldBe(401);
        });
    }

    [Fact]
    public async Task PingWithValidKeyReturns200()
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/ping");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, _testRawKey);
            s.StatusCodeShouldBe(200);
        });

        var body = result.ReadAsText();
        Assert.Equal("pong", body);
    }

    [Fact]
    public async Task HealthWithoutKeyReturns200()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/health");
            s.StatusCodeShouldBe(200);
        });
    }

    [Fact]
    public async Task SwaggerWithoutKeyReturns200()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/swagger/v1/swagger.json");
            s.StatusCodeShouldBe(200);
        });
    }
}
