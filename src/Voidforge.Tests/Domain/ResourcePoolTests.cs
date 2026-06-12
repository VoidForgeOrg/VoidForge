using Voidforge.Api.Domain;
using Xunit;

namespace Voidforge.Tests.Domain;

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
