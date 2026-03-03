using Alba;
using Xunit;

namespace Voidforge.Tests;

[Collection(IntegrationCollection.Name)]
public sealed class HealthCheckTests
{
    private readonly IAlbaHost _host;

    public HealthCheckTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task HealthEndpointReturns200()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/health");
            s.StatusCodeShouldBe(200);
        });
    }
}
