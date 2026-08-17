using Alba;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Xunit;

namespace Voidforge.Tests.Colonize;

// #51 (closes #19): registration's homeworld assignment now goes through the same
// FetchForWriting + null-owner-check guard shape as the fleet Colonize claim (Planet.Claim,
// D10), wrapped in a bounded re-pick retry with a fresh Marten session per attempt (a failed
// SaveChangesAsync can't be selectively unwound on a shared session). SequentialRegistration...
// below is the mechanism smoke test: a plain, uncontested registration must still succeed
// end-to-end exactly as before the refactor.
// #51 (spec §7 item 4; #50 final-review carry-over): the real concurrency coverage —
// two fleets racing to colonize the SAME planet (exactly one winner; exact-equality
// conservation of the loaded cargo) and five concurrent registrations that never colonize the
// same homeworld twice. ContestedPlanetAppendLosesWithConcurrencyExceptionDeterministically
// below (#52) strengthens the five-concurrent-registrations coverage with a deterministic,
// non-probabilistic proof of the same version guard.
[Trait("Category", "Integration")]
[Collection(IntegrationCollection.Name)]
public sealed class ClaimRaceTests
{
    // Cargo amounts loaded aboard each racing fleet in the two-fleet colonize race — small
    // enough to fit a CargoVessel's capacity, non-zero enough to make the conservation
    // assertions meaningful.
    private const decimal _raceCargoIronOre = 100m;
    private const decimal _raceCargoIronIngot = 50m;

    private readonly IAlbaHost _host;

    public ClaimRaceTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task SequentialRegistrationStillSucceedsAndOwnsItsHomeworld()
    {
        var registration = await _host.RegisterPlayer("ClaimRace_Test_");

        Assert.NotEqual(Guid.Empty, registration.PlayerId);
        Assert.StartsWith("vf_", registration.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, registration.HomeworldId);

        var homeworld = await _host.GetPlanetById(registration, registration.HomeworldId);
        Assert.Equal(registration.PlayerId, homeworld.OwnerId);
        Assert.True(homeworld.IronOre.CurrentValue > 0);
        Assert.True(homeworld.IronIngot.CurrentValue > 0);
    }

    [Fact]
    public async Task FiveConcurrentRegistrationsAllSucceedWithDistinctHomeworldsOwnedByTheirRegistrants()
    {
        var registrations = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => _host.RegisterPlayer("ClaimRace_Test_")));

        // All five requests carry distinct auto-generated names (RegisterPlayer's Guid
        // suffix) and none may collide on a homeworld — the guarded claim (#51, closes #19)
        // exists precisely to make that true even when five registrations race the same
        // uncolonized-planet pool concurrently.
        Assert.Equal(5, registrations.Select(r => r.HomeworldId).Distinct().Count());

        foreach (var registration in registrations)
        {
            var homeworld = await _host.GetPlanetById(registration, registration.HomeworldId);
            Assert.Equal(registration.PlayerId, homeworld.OwnerId);
        }
    }

    // Deterministic counterpart to FiveConcurrentRegistrationsAllSucceedWithDistinctHomeworldsOwnedByTheirRegistrants
    // above (#52): that test only catches a collision probabilistically (~5% odds per run
    // against unguarded code, since five concurrent registrations rarely pick the same
    // planet out of dozens of uncolonized candidates). This test instead forces the collision
    // by hand — two sessions FetchForWriting the SAME stream before either saves, so both
    // capture the same expected starting version — which deterministically proves the
    // version guard that both D10 claim sites (Planet.Claim's fleet-Colonize call and
    // PlayerEndpoints' registration claim) rely on. It also pins the BASE exception type:
    // Program.cs's #39 durable-message retry policy and PlayerEndpoints' TryClaimHomeworld
    // catch clause both key off JasperFx.ConcurrencyException, not the more specific
    // JasperFx.Events.EventStreamUnexpectedMaxEventIdException Marten actually throws here —
    // asserting against the base type guards against a future Marten/JasperFx upgrade
    // narrowing (or changing) the concrete exception type from under that catch machinery.
    [Fact]
    public async Task ContestedPlanetAppendLosesWithConcurrencyExceptionDeterministically()
    {
        var registration = await _host.RegisterPlayer("ClaimRace_Test_");
        var planetId = await _host.FindUncolonizedPlanet(registration);

        var store = _host.Services.GetRequiredService<IDocumentStore>();

        await using var sessionB = store.LightweightSession();
        await using var sessionA = store.LightweightSession();

        // Both sessions fetch before either saves, so both arm their optimistic-concurrency
        // guard against the same starting stream version — the fetch order between A and B
        // doesn't matter, only that neither has saved yet when the other fetches.
        var streamB = await sessionB.Events.FetchForWriting<Planet>(planetId);
        var streamA = await sessionA.Events.FetchForWriting<Planet>(planetId);

        streamB.AppendOne(new PlanetColonized(Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow));
        await sessionB.SaveChangesAsync();

        streamA.AppendOne(new PlanetColonized(Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow));
        await Assert.ThrowsAnyAsync<ConcurrencyException>(() => sessionA.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoFleetsRacingToColonizeTheSamePlanetYieldExactlyOneWinnerWithConservedCargo()
    {
        var alpha = await _host.RegisterPlayer("ClaimRace_Test_");
        var beta = await _host.RegisterPlayer("ClaimRace_Test_");
        var destinationId = await _host.FindUncolonizedPlanet(alpha);

        var (alphaFleet, alphaArrivesAt) = await BuildAndLaunchColonizeFleet(alpha, destinationId);
        var (betaFleet, betaArrivesAt) = await BuildAndLaunchColonizeFleet(beta, destinationId);

        // Fire both handler-invoked arrivals CONCURRENTLY, one fresh LightweightSession per
        // call (spec §7 testing strategy item 2) — this is what actually exercises the D10
        // guard's tie-breaking rather than two sequential, non-racing claims.
        await Task.WhenAll(
            _host.CompleteArrivalWithRetry(alphaFleet.Id, alphaArrivesAt),
            _host.CompleteArrivalWithRetry(betaFleet.Id, betaArrivesAt));

        var destination = await _host.GetPlanetById(alpha, destinationId);
        Assert.True(
            destination.OwnerId == alpha.PlayerId || destination.OwnerId == beta.PlayerId,
            "Exactly one racer must own the destination planet after both arrivals resolve.");

        var winnerIsAlpha = destination.OwnerId == alpha.PlayerId;
        var winner = winnerIsAlpha ? alpha : beta;
        var loser = winnerIsAlpha ? beta : alpha;
        var winnerFleetId = winnerIsAlpha ? alphaFleet.Id : betaFleet.Id;
        var loserFleetId = winnerIsAlpha ? betaFleet.Id : alphaFleet.Id;

        var winnerFleet = await _host.GetJson<FleetResponse>(winner, $"/api/fleets/{winnerFleetId}");
        var loserFleet = await _host.GetJson<FleetResponse>(loser, $"/api/fleets/{loserFleetId}");

        // Winner: the Colony Ship is consumed (each fleet carried exactly one Colony Ship, so
        // it is trivially also the oldest — ConsumeColonyShip's deterministic pick is proven
        // against multiple ships in ColonizeDomainTests), cargo delivered and zeroed.
        Assert.Equal(FleetStatus.Stationed, winnerFleet.Status);
        Assert.Equal(destinationId, winnerFleet.LocationPlanetId);
        var winnerShip = Assert.Single(winnerFleet.Ships);
        Assert.Equal(ShipType.CargoVessel, winnerShip.Type);
        Assert.Equal(0m, winnerFleet.CargoIronOre);
        Assert.Equal(0m, winnerFleet.CargoIronIngot);

        // Loser: Apply(ColonizationFailed) is a state no-op (decision 4) — ship and cargo both
        // survive intact, the fleet still idles Stationed at the (now foreign) destination.
        Assert.Equal(FleetStatus.Stationed, loserFleet.Status);
        Assert.Equal(destinationId, loserFleet.LocationPlanetId);
        Assert.Equal(2, loserFleet.Ships.Count);
        Assert.Contains(loserFleet.Ships, s => s.Type == ShipType.ColonyShip);
        Assert.Contains(loserFleet.Ships, s => s.Type == ShipType.CargoVessel);
        Assert.Equal(_raceCargoIronOre, loserFleet.CargoIronOre);
        Assert.Equal(_raceCargoIronIngot, loserFleet.CargoIronIngot);

        // Conservation (#50 final-review carry-over, spec §7 item 4): a freshly claimed
        // colony starts at zero stores with zero production (Planet.Claim), so EXACT equality
        // — not a tolerance — is safe both for the destination's stores and for the loser's
        // untouched cargo. The total across the destination plus both fleets' remaining cargo
        // must equal everything loaded at assembly (200 ore / 100 ingot).
        Assert.Equal(_raceCargoIronOre, destination.IronOre.CurrentValue);
        Assert.Equal(_raceCargoIronIngot, destination.IronIngot.CurrentValue);
        Assert.Equal(_raceCargoIronOre * 2, destination.IronOre.CurrentValue + winnerFleet.CargoIronOre + loserFleet.CargoIronOre);
        Assert.Equal(_raceCargoIronIngot * 2, destination.IronIngot.CurrentValue + winnerFleet.CargoIronIngot + loserFleet.CargoIronIngot);
    }

    // Builds an operational shipyard, queues one Colony Ship + one Cargo Vessel, waits for
    // stock to recover past the race's 100/50 cargo load, assembles a fleet with both ships
    // and that cargo, then launches Colonize at the shared destination. Returns the launched
    // fleet and its ArrivesAt so the caller can drive the handler-invoked arrival directly
    // (spec §7 testing strategy item 2) with the exact scheduled timestamp.
    private async Task<(FleetResponse Fleet, DateTimeOffset ArrivesAt)> BuildAndLaunchColonizeFleet(
        RegisterPlayerResponse registration, Guid destinationId)
    {
        var colonyShipId = await _host.BuildRosterShip(registration, ShipType.ColonyShip);
        var cargoVesselId = await _host.BuildRosterShip(registration, ShipType.CargoVessel);
        await _host.WaitForStock(registration, 150m, 100m);
        var fleet = await _host.AssembleFleet(
            registration, [colonyShipId, cargoVesselId], new CargoRequest(_raceCargoIronOre, _raceCargoIronIngot));

        var launched = await _host.Launch(registration, fleet.Id, MissionType.Colonize, destinationId);
        Assert.NotNull(launched.ArrivesAt);

        return (launched, launched.ArrivesAt.Value);
    }
}
