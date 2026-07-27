using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Cargo;

public sealed class PlanetStorageMutationTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A freshly created, uncolonized planet: empty pools, generous capacity, no buildings.
    private static Planet BarePlanet(long capacity = 1000)
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, capacity, capacity, 0m, 0m, 0m));
        return planet;
    }

    // Planet with an operational Generator (so the productivity multiplier is 1, not 0 from
    // unmet demand) and Drill (10 ore/s per BuildingSpecs) placed at _t0 — used to prove the
    // checkpoint-then-subtract ordering against a genuinely accruing pool.
    private static Planet PlanetWithDrill(long capacity = 1000)
    {
        var planet = BarePlanet(capacity);
        planet.Apply(new BuildingPlaced(BuildingType.Generator, _t0));
        planet.Apply(new BuildingPlaced(BuildingType.Drill, _t0));
        return planet;
    }

    // A planet whose IronOre pool is checkpointed at a specific value/time — used for the
    // headroom and backwards-`at` delivery scenarios where accrual isn't the point.
    private static Planet PlanetWithIronOreStock(decimal value, long capacity, DateTimeOffset checkpointAt)
    {
        var planet = BarePlanet(capacity);
        planet.Apply(new PlanetColonized(Guid.NewGuid(), (long)value, 0, checkpointAt));
        return planet;
    }

    [Fact]
    public void LoadLocksInAccrualThenSubtracts()
    {
        var planet = PlanetWithDrill();
        var at = _t0.AddSeconds(10); // Drill accrues 10/s -> 100 ore by `at`.

        var @event = planet.LoadCargoFromStorage(Guid.NewGuid(), 60m, 0m, at);
        planet.Apply(@event);

        // 100 accrued, minus 60 loaded = 40, checkpointed exactly at `at`.
        Assert.Equal(40m, planet.IronOre.CheckpointValue);
        Assert.Equal(at, planet.IronOre.CheckpointTime);
    }

    [Fact]
    public void LoadEventCarriesFleetIdAndAmounts()
    {
        var planet = PlanetWithDrill();
        var fleetId = Guid.NewGuid();
        var at = _t0.AddSeconds(10);

        var @event = planet.LoadCargoFromStorage(fleetId, 60m, 0m, at);

        Assert.Equal(fleetId, @event.FleetId);
        Assert.Equal(60m, @event.IronOre);
        Assert.Equal(0m, @event.IronIngot);
        Assert.Equal(at, @event.At);
    }

    [Fact]
    public void LoadThrowsWhenIronOreAmountIsNegative()
    {
        var planet = PlanetWithDrill();

        Assert.Throws<InvalidOperationException>(
            () => planet.LoadCargoFromStorage(Guid.NewGuid(), -1m, 0m, _t0.AddSeconds(10)));
    }

    [Fact]
    public void LoadThrowsWhenIronIngotAmountIsNegative()
    {
        var planet = PlanetWithDrill();

        Assert.Throws<InvalidOperationException>(
            () => planet.LoadCargoFromStorage(Guid.NewGuid(), 0m, -1m, _t0.AddSeconds(10)));
    }

    [Fact]
    public void LoadThrowsWhenIronOreAmountExceedsCurrentValue()
    {
        var planet = PlanetWithDrill();
        var at = _t0.AddSeconds(10); // Only 100 ore accrued by `at`.

        Assert.Throws<InvalidOperationException>(
            () => planet.LoadCargoFromStorage(Guid.NewGuid(), 150m, 0m, at));
    }

    [Fact]
    public void LoadThrowsWhenIronIngotAmountExceedsCurrentValue()
    {
        var planet = PlanetWithDrill(); // No refinery -> IronIngot stays at 0.

        Assert.Throws<InvalidOperationException>(
            () => planet.LoadCargoFromStorage(Guid.NewGuid(), 0m, 1m, _t0.AddSeconds(10)));
    }

    [Fact]
    public void RateIsUnchangedAfterLoadApply()
    {
        var planet = PlanetWithDrill();
        var at = _t0.AddSeconds(10);
        var rateBefore = planet.IronOre.Rate;

        planet.Apply(planet.LoadCargoFromStorage(Guid.NewGuid(), 60m, 0m, at));

        Assert.Equal(rateBefore, planet.IronOre.Rate);
        Assert.Equal(10m, planet.IronOre.Rate);
    }

    [Fact]
    public void DeliveryAddsAcceptedAmountToPool()
    {
        var planet = PlanetWithIronOreStock(100m, 1000, _t0);
        var at = _t0.AddSeconds(5);

        var @event = planet.AcceptCargoDelivery(Guid.NewGuid(), 50m, 0m, at);
        planet.Apply(@event);

        Assert.Equal(150m, planet.IronOre.CheckpointValue);
        Assert.Equal(at, planet.IronOre.CheckpointTime);
    }

    [Fact]
    public void DeliveryToExactlyFullDestinationAcceptsZero()
    {
        var planet = PlanetWithIronOreStock(1000m, 1000, _t0); // Already at capacity.
        var at = _t0.AddSeconds(5);

        var @event = planet.AcceptCargoDelivery(Guid.NewGuid(), 50m, 0m, at);

        Assert.Equal(0m, @event.IronOre);
    }

    [Fact]
    public void DeliveryWithPartialHeadroomAcceptsExactlyTheHeadroom()
    {
        var planet = PlanetWithIronOreStock(980m, 1000, _t0); // Headroom = 20.
        var at = _t0.AddSeconds(5);

        // Offer (50) exceeds headroom (20) -> clamps to the headroom.
        var @event = planet.AcceptCargoDelivery(Guid.NewGuid(), 50m, 0m, at);
        planet.Apply(@event);

        Assert.Equal(20m, @event.IronOre);
        Assert.Equal(1000m, planet.IronOre.CheckpointValue);
    }

    [Fact]
    public void DeliveryOfferUnderHeadroomAcceptsTheFullOffer()
    {
        var planet = PlanetWithIronOreStock(980m, 1000, _t0); // Headroom = 20.
        var at = _t0.AddSeconds(5);

        var @event = planet.AcceptCargoDelivery(Guid.NewGuid(), 15m, 0m, at);

        Assert.Equal(15m, @event.IronOre);
    }

    [Fact]
    public void RateIsUnchangedAfterDeliveryApply()
    {
        var planet = PlanetWithDrill(); // Nonzero IronOre.Rate from the Drill.
        var at = _t0.AddSeconds(5);
        var rateBefore = planet.IronOre.Rate;

        planet.Apply(planet.AcceptCargoDelivery(Guid.NewGuid(), 10m, 0m, at));

        Assert.Equal(rateBefore, planet.IronOre.Rate);
        Assert.Equal(10m, planet.IronOre.Rate);
    }

    [Fact]
    public void BackwardsAtDeliveryAdjustsValueWithoutRegressingCheckpointTime()
    {
        var planet = PlanetWithIronOreStock(100m, 1000, _t0); // Checkpointed at _t0.
        var backwardsAt = _t0.AddSeconds(-5);

        var @event = planet.AcceptCargoDelivery(Guid.NewGuid(), 30m, 0m, backwardsAt);
        planet.Apply(@event);

        Assert.Equal(30m, @event.IronOre);
        Assert.Equal(130m, planet.IronOre.CheckpointValue);
        Assert.Equal(_t0, planet.IronOre.CheckpointTime); // Not regressed to backwardsAt.
    }

    [Fact]
    public void DeliveryThrowsWhenIronOreAmountIsNegative()
    {
        var planet = PlanetWithDrill();

        Assert.Throws<InvalidOperationException>(
            () => planet.AcceptCargoDelivery(Guid.NewGuid(), -1m, 0m, _t0.AddSeconds(10)));
    }

    [Fact]
    public void DeliveryThrowsWhenIronIngotAmountIsNegative()
    {
        var planet = PlanetWithDrill();

        Assert.Throws<InvalidOperationException>(
            () => planet.AcceptCargoDelivery(Guid.NewGuid(), 0m, -1m, _t0.AddSeconds(10)));
    }
}
