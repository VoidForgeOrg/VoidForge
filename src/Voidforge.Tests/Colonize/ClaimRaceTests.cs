using Alba;
using Voidforge.Api.Auth;
using Voidforge.Api.Endpoints;
using Xunit;

namespace Voidforge.Tests.Colonize;

// Task 4 (#51, closes #19): registration's homeworld assignment now goes through the same
// FetchForWriting + null-owner-check guard shape as the fleet Colonize claim (Planet.Claim,
// D10), wrapped in a bounded re-pick retry with a fresh Marten session per attempt (a failed
// SaveChangesAsync can't be selectively unwound on a shared session). This file's job right
// now is the mechanism smoke test: a plain, uncontested registration must still succeed
// end-to-end exactly as before the refactor. Task 5 adds the real concurrency coverage
// (two-fleet colonize race + conservation; concurrent registrations never double-colonize) —
// leave room below for those.
[Collection(IntegrationCollection.Name)]
public sealed class ClaimRaceTests
{
    private readonly IAlbaHost _host;

    public ClaimRaceTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task SequentialRegistrationStillSucceedsAndOwnsItsHomeworld()
    {
        var registration = await RegisterPlayer();

        Assert.NotEqual(Guid.Empty, registration.PlayerId);
        Assert.StartsWith("vf_", registration.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, registration.HomeworldId);

        var homeworld = await GetPlanetById(registration, registration.HomeworldId);
        Assert.Equal(registration.PlayerId, homeworld.OwnerId);
        Assert.True(homeworld.IronOre.CurrentValue > 0);
        Assert.True(homeworld.IronIngot.CurrentValue > 0);
    }

    private async Task<PlanetResponse> GetPlanetById(RegisterPlayerResponse asWhom, Guid planetId)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/planets/{planetId}");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, asWhom.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<PlanetResponse>();
        Assert.NotNull(response);
        return response;
    }

    private async Task<RegisterPlayerResponse> RegisterPlayer()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"ClaimRace_Test_{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }
}
