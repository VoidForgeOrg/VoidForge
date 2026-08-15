using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Voidforge.Api.Endpoints;
using Voidforge.Tests.Support;
using Wolverine;
using Xunit;

namespace Voidforge.Tests.Halting;

// Storage-full halting (#69). These drive CheckStorageFullHandler.Handle DIRECTLY at a chosen
// predicted instant (mirrors IntegrationApiExtensions.LaunchAndArriveInstantly) rather than
// waiting ~1900s for the homeworld Drill's +5/s to fill the 10000-cap ore pool by wall clock.
[Collection(IntegrationCollection.Name)]
public sealed class StorageHaltingTests
{
    private readonly IAlbaHost _host;

    public StorageHaltingTests(AppFixture fixture)
    {
        _host = fixture.Host;
    }

    [Fact]
    public async Task ProducerHaltsWhenOutputPoolAtCapacity()
    {
        var registration = await _host.RegisterPlayer("StorageHalt_Full_");
        var planetId = registration.HomeworldId;
        var before = await _host.GetPlanet(registration);

        var drillBefore = before.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Operational, drillBefore.Status);

        var at = DateTimeOffset.UtcNow;
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // Fill the ore pool to capacity deterministically: Apply(CargoDeliveredToStorage) clamps the
        // stored value to StorageCapacity, so a single oversized delivery pins the pool at its cap.
        await using (var seedSession = store.LightweightSession())
        {
            var seedStream = await seedSession.Events.FetchForWriting<Planet>(planetId);
            seedStream.AppendOne(new CargoDeliveredToStorage(
                Guid.NewGuid(), before.IronOre.StorageCapacity, 0m, at));
            await seedSession.SaveChangesAsync();
        }

        // Validate-on-arrival at `at`: the ore pool is at cap, so the Operational Drill halts.
        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = store.LightweightSession();
            await CheckStorageFullHandler.Handle(
                new CheckStorageFull(planetId, ResourceType.IronOre, at), session, bus);
        }

        var after = await _host.GetPlanet(registration);
        var drillAfter = after.Buildings.Single(b => b.Type == BuildingType.Drill);

        Assert.Equal(BuildingStatus.Halted, drillAfter.Status);
        Assert.Equal(HaltReason.OutputStorageFull, drillAfter.HaltReason);
        // Halted Drill leaves the Operational set: drill inflow stops and its energy draw drops from
        // the full 20 MW to the 5% idle floor. The homeworld Refinery is still Operational and the ore
        // buffer is at cap, so it now draws the buffer down at its 5/s demand (#70 buffer-drain): the
        // net ore rate is -5 (was 0 under the old inflow-only clamp).
        Assert.Equal(-5m, after.IronOre.Rate);
        Assert.True(
            after.Energy.ConsumptionMw < before.Energy.ConsumptionMw,
            $"Halted Drill should drop energy draw: before={before.Energy.ConsumptionMw}, after={after.Energy.ConsumptionMw}.");
    }

    [Fact]
    public async Task ProducerResumesWhenCargoLoadFreesOutputStorage()
    {
        var registration = await _host.RegisterPlayer("StorageHalt_Resume_");
        var planetId = registration.HomeworldId;

        // A roster CargoVessel to carry ore off-planet (builds an operational shipyard first).
        // Done before halting so the Drill produces normally during the wall-clock build.
        var shipId = await _host.BuildRosterShip(registration, ShipType.CargoVessel);

        var before = await _host.GetPlanet(registration);
        var at = DateTimeOffset.UtcNow;
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // Pin ore at capacity (oversized delivery clamps to cap), then halt the Drill via a
        // validate-on-arrival CheckStorageFull at `at` — same technique as the halt test above.
        await using (var seedSession = store.LightweightSession())
        {
            var seedStream = await seedSession.Events.FetchForWriting<Planet>(planetId);
            seedStream.AppendOne(new CargoDeliveredToStorage(
                Guid.NewGuid(), before.IronOre.StorageCapacity, 0m, at));
            await seedSession.SaveChangesAsync();
        }

        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = store.LightweightSession();
            await CheckStorageFullHandler.Handle(
                new CheckStorageFull(planetId, ResourceType.IronOre, at), session, bus);
        }

        var halted = await _host.GetPlanet(registration);
        var drillHalted = halted.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Halted, drillHalted.Status);
        Assert.Equal(HaltReason.OutputStorageFull, drillHalted.HaltReason);
        // Drill inflow stopped, but the still-Operational Refinery draws the full buffer at 5/s (#70).
        Assert.Equal(-5m, halted.IronOre.Rate);

        // Load ore off the planet: freeing output storage must resume the Drill in the SAME
        // commit (D6) — no scheduled message, no wall-clock wait.
        await _host.AssembleFleet(registration, [shipId], new CargoRequest(10m, 0m));

        var after = await _host.GetPlanet(registration);
        var drillAfter = after.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Operational, drillAfter.Status);
        Assert.Null(drillAfter.HaltReason);
        Assert.True(
            after.IronOre.Rate > 0m,
            $"Resumed Drill should produce ore again: rate={after.IronOre.Rate}.");
        // Storage was genuinely freed (below cap) by ~the loaded amount. The Refinery drains the buffer
        // at 5/s while the Drill is halted (#70), so the post-load value sits a little below cap-10
        // (buffer drain + the 10-unit load); a double-applied load (UseIdentityMapForAggregates) would
        // land near cap-20, which the loosened floor still excludes.
        var cap = before.IronOre.StorageCapacity;
        Assert.True(
            after.IronOre.CurrentValue < cap,
            $"Ore should be below cap after loading: {after.IronOre.CurrentValue} / {cap}.");
        Assert.True(
            after.IronOre.CurrentValue > cap - 20m,
            $"Ore should reflect a single 10-unit load (near cap-10, minus buffer drain), not a " +
            $"double-applied one (~cap-20): {after.IronOre.CurrentValue} / {cap}.");
    }

    [Fact]
    public async Task StaleCheckWhenPoolNotFullAppendsNothing()
    {
        var registration = await _host.RegisterPlayer("StorageHalt_Stale_");
        var planetId = registration.HomeworldId;
        var before = await _host.GetPlanet(registration);

        var drillBefore = before.Buildings.Single(b => b.Type == BuildingType.Drill);
        Assert.Equal(BuildingStatus.Operational, drillBefore.Status);
        Assert.True(
            before.IronOre.CurrentValue < before.IronOre.StorageCapacity,
            "Precondition: the ore pool must start below capacity.");

        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // The pool is below cap at this instant — a superseded/mis-predicted check is a no-op.
        using (var scope = _host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = store.LightweightSession();
            await CheckStorageFullHandler.Handle(
                new CheckStorageFull(planetId, ResourceType.IronOre, DateTimeOffset.UtcNow), session, bus);
        }

        var after = await _host.GetPlanet(registration);
        var drillAfter = after.Buildings.Single(b => b.Type == BuildingType.Drill);

        Assert.Equal(BuildingStatus.Operational, drillAfter.Status);
        Assert.Null(drillAfter.HaltReason);
    }
}
