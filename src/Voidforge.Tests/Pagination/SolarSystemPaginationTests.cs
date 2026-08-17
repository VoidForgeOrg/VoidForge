using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Pagination;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Pagination;

[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class SolarSystemPaginationTests
{
    private readonly IAlbaHost _host;

    public SolarSystemPaginationTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ReturnsPagedEnvelopeOrderedByName()
    {
        var apiKey = (await _host.RegisterPlayer("Pg_Test_")).ApiKey;

        var page = await GetPage(apiKey, "?page=1&pageSize=5");

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(5, page.PageSize);
        Assert.True(page.TotalItems >= 40);            // fixture seeds 80 systems
        Assert.False(page.HasPrevious);
        Assert.True(page.HasNext);
        // Deterministic order: names non-decreasing.
        var names = page.Items.Select(i => i.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task SecondPageHasPreviousAndDiffersFromFirst()
    {
        var apiKey = (await _host.RegisterPlayer("Pg_Test_")).ApiKey;

        var first = await GetPage(apiKey, "?page=1&pageSize=5");
        var second = await GetPage(apiKey, "?page=2&pageSize=5");

        Assert.True(second.HasPrevious);
        Assert.Equal(2, second.Page);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
    }

    [Fact]
    public async Task ClampsPageSizeToMaximum()
    {
        var apiKey = (await _host.RegisterPlayer("Pg_Test_")).ApiKey;

        var page = await GetPage(apiKey, "?pageSize=500");

        Assert.Equal(200, page.PageSize);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=-1")]
    public async Task RejectsInvalidParameters(string query)
    {
        var apiKey = (await _host.RegisterPlayer("Pg_Test_")).ApiKey;

        await _host.Scenario(s =>
        {
            s.Get.Url($"/api/solar-systems{query}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, apiKey);
            s.StatusCodeShouldBe(400);
        });
    }

    private async Task<PagedResponse<SolarSystemResponse>> GetPage(string apiKey, string query)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/solar-systems{query}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, apiKey);
            s.StatusCodeShouldBe(200);
        });

        var page = await result.ReadAsJsonAsync<PagedResponse<SolarSystemResponse>>();
        Assert.NotNull(page);
        return page;
    }
}
