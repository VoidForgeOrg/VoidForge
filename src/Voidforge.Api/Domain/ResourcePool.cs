namespace Voidforge.Api.Domain;

public sealed record ResourcePool(
    decimal CheckpointValue,
    decimal Rate,
    decimal StorageCapacity,
    DateTimeOffset CheckpointTime)
{
    // Event `at` timestamps are not guaranteed to be non-decreasing along a Planet stream (#44):
    // a completion scheduled for T can be delivered after a player command already committed at
    // W > T (durable-message poll lag per ADR 0001, plus the #39 ConcurrencyException retry
    // backoff). Flooring elapsed at 0 makes such a rewound read inert — without it a negative
    // elapsed silently drains an accruing pool, and (rate < 0) * (elapsed < 0) fabricates
    // resources outright. Math.Clamp bounds the stored value but does not make it correct.
    public decimal GetCurrentValue(DateTimeOffset now)
    {
        var elapsed = Math.Max(0m, (decimal)(now - CheckpointTime).TotalSeconds);
        return Math.Clamp(CheckpointValue + Rate * elapsed, 0, StorageCapacity);
    }

    // Non-regressing for the same reason: letting CheckpointTime move backwards would re-accrue
    // the rewound interval on every subsequent read, compounding the error rather than absorbing
    // it once. A backwards checkpoint is therefore a no-op, not a rewind.
    public ResourcePool Checkpoint(DateTimeOffset now)
    {
        return this with
        {
            CheckpointValue = GetCurrentValue(now),
            CheckpointTime = now > CheckpointTime ? now : CheckpointTime,
        };
    }
}
