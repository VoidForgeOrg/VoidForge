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
        // Halted Drill leaves the Operational set: ore production stops and its energy draw drops
        // from the full 20 MW to the 5% idle floor.
        Assert.Equal(0m, after.IronOre.Rate);
        Assert.True(
            after.Energy.ConsumptionMw < before.Energy.ConsumptionMw,
            $"Halted Drill should drop energy draw: before={before.Energy.ConsumptionMw}, after={after.Energy.ConsumptionMw}.");
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
