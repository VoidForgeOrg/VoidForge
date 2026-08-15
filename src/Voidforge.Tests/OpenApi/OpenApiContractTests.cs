using System.Text.Json;
using Alba;
using Xunit;

namespace Voidforge.Tests.OpenApi;

/// <summary>
/// Guards the acceptance criterion "OpenAPI document reflects all Phase 5 endpoints" (#74).
/// Fetches the live Swagger document from the running host and asserts every current API
/// operation (path + HTTP method) is present, so a newly-added endpoint that is missing from
/// the emitted contract fails the build instead of silently drifting (as the committed
/// frontend snapshot did — it was 14 operations behind before this test existed).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OpenApiContractTests
{
    private readonly IAlbaHost _host;

    // Every operation the API currently exposes, as (route template, HTTP method) pairs.
    // The route templates are the exact strings from the [Wolverine*] endpoint attributes,
    // which Swashbuckle emits verbatim as OpenAPI path keys.
    private static readonly (string Path, string Method)[] _expectedOperations =
    [
        // Phase 1 — baseline surface
        ("/api/ping", "get"),
        ("/api/planets/{id}", "get"),
        ("/api/players/register", "post"),
        ("/api/players/me", "get"),
        ("/api/solar-systems", "get"),

        // Phase 2-3 — ships & buildings
        ("/api/planets/{planetId}/ship-queue", "post"),
        ("/api/planets/{planetId}/ship-queue", "get"),
        ("/api/planets/{planetId}/ship-queue/{buildId}", "delete"),
        ("/api/planets/{planetId}/ships", "get"),
        ("/api/planets/{planetId}/buildings", "post"),
        ("/api/planets/{planetId}/buildings/{slotIndex}/construction", "delete"),
        ("/api/planets/{planetId}/buildings/{slotIndex}/demolish", "post"),

        // Phase 4-5 — fleets & missions
        ("/api/fleets/{fleetId}/missions", "post"),
        ("/api/planets/{planetId}/fleets", "post"),
        ("/api/planets/{planetId}/fleets", "get"),
        ("/api/fleets/{fleetId}/disband", "post"),
        ("/api/fleets/{fleetId}/cancel", "post"),
        ("/api/fleets/{fleetId}/unload", "post"),
        ("/api/fleets", "get"),
        ("/api/fleets/{fleetId}", "get"),
    ];

    public OpenApiContractTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task SwaggerDocumentContainsEveryCurrentApiOperation()
    {
        // The Swagger endpoint is anonymous (see ApiKeyAuthTests.SwaggerWithoutKeyReturns200).
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/swagger/v1/swagger.json");
            s.StatusCodeShouldBe(200);
        });

        using var document = JsonDocument.Parse(result.ReadAsText());
        var root = document.RootElement;

        Assert.True(
            root.TryGetProperty("paths", out var paths),
            "OpenAPI document is missing the 'paths' object.");

        var missing = new List<string>();
        foreach (var (path, method) in _expectedOperations)
        {
            if (!paths.TryGetProperty(path, out var pathItem)
                || !pathItem.TryGetProperty(method, out _))
            {
                missing.Add($"{method.ToUpperInvariant()} {path}");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"OpenAPI document is missing {missing.Count} operation(s): {string.Join(", ", missing)}");
    }
}
