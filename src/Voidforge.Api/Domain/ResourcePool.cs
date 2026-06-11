namespace Voidforge.Api.Domain;

public sealed record ResourcePool(
    decimal CheckpointValue,
    decimal Rate,
    decimal StorageCapacity,
    DateTimeOffset CheckpointTime)
{
    public decimal GetCurrentValue(DateTimeOffset now)
    {
        var elapsed = (decimal)(now - CheckpointTime).TotalSeconds;
        return Math.Clamp(CheckpointValue + Rate * elapsed, 0, StorageCapacity);
    }

    public ResourcePool Checkpoint(DateTimeOffset now)
    {
        return this with { CheckpointValue = GetCurrentValue(now), CheckpointTime = now };
    }
}
