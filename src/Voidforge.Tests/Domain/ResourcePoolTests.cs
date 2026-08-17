using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Domain;

[Trait("Category", "Unit")]
public sealed class ResourcePoolTests
{
    [Fact]
    public void AccumulatesOverTime()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);

        Assert.Equal(150m, pool.GetCurrentValue(checkpoint.AddSeconds(5)));
    }

    [Fact]
    public void ConsumesOverTime()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(200, -5, 1000, checkpoint);

        Assert.Equal(150m, pool.GetCurrentValue(checkpoint.AddSeconds(10)));
    }

    [Fact]
    public void ClampsAtZero()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(50, -10, 1000, checkpoint);

        Assert.Equal(0m, pool.GetCurrentValue(checkpoint.AddSeconds(10)));
    }

    [Fact]
    public void ClampsAtCapacity()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(900, 50, 1000, checkpoint);

        Assert.Equal(1000m, pool.GetCurrentValue(checkpoint.AddSeconds(10)));
    }

    [Fact]
    public void CheckpointResetsBaseline()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);
        var newTime = checkpoint.AddSeconds(5);

        var checkpointed = pool.Checkpoint(newTime);

        Assert.Equal(150m, checkpointed.CheckpointValue);
        Assert.Equal(newTime, checkpointed.CheckpointTime);
        Assert.Equal(10m, checkpointed.Rate);
        Assert.Equal(1000m, checkpointed.StorageCapacity);
    }

    [Fact]
    public void ZeroRateReturnsCheckpointValue()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(500, 0, 10000, checkpoint);

        Assert.Equal(500m, pool.GetCurrentValue(checkpoint.AddSeconds(100)));
    }

    // #44: event `at` timestamps are not guaranteed monotonic along a Planet stream (a late
    // completion can commit after a newer command). A backwards `now` must be inert, never
    // negative-accrual and never a regressed checkpoint.
    [Fact]
    public void BackwardsTimeDoesNotDrainAnAccruingPool()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);

        // Unfloored elapsed would give 100 + 10 * (-5) = 50.
        Assert.Equal(100m, pool.GetCurrentValue(checkpoint.AddSeconds(-5)));
    }

    [Fact]
    public void BackwardsTimeDoesNotFabricateResourcesInADrainingPool()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, -10, 1000, checkpoint);

        // Negative rate * negative elapsed invents resources: 100 + (-10) * (-5) = 150.
        Assert.Equal(100m, pool.GetCurrentValue(checkpoint.AddSeconds(-5)));
    }

    [Fact]
    public void CheckpointDoesNotRegressTime()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);

        var checkpointed = pool.Checkpoint(checkpoint.AddSeconds(-5));

        // A regressed CheckpointTime re-accrues the rewound interval on every later read.
        Assert.Equal(checkpoint, checkpointed.CheckpointTime);
        Assert.Equal(100m, checkpointed.CheckpointValue);
    }

    [Fact]
    public void CheckpointAtSameInstantIsIdempotent()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);

        var checkpointed = pool.Checkpoint(checkpoint);

        Assert.Equal(checkpoint, checkpointed.CheckpointTime);
        Assert.Equal(100m, checkpointed.CheckpointValue);
    }

    [Fact]
    public void IsImmutable()
    {
        var checkpoint = DateTimeOffset.UtcNow;
        var pool = new ResourcePool(100, 10, 1000, checkpoint);

        var checkpointed = pool.Checkpoint(checkpoint.AddSeconds(5));

        Assert.Equal(100m, pool.CheckpointValue);
        Assert.Equal(checkpoint, pool.CheckpointTime);
        Assert.NotSame(pool, checkpointed);
    }
}
