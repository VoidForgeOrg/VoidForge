using System.Collections.Concurrent;

namespace Voidforge.SoakTests;

// Thread-safe collector shared by the two concurrent user scripts and the deposit-snapshot loop.
public sealed class SoakRecorder
{
    private readonly ConcurrentBag<int> _statuses = [];
    private readonly ConcurrentBag<string> _events = [];
    private readonly ConcurrentBag<IntermediateSnapshot> _snapshots = [];

    // Raw HTTP status codes from deliberately-raced calls (PostForStatus / CancelForStatus) — the
    // trustworthy signal for I4/I5.
    public void RecordStatus(int statusCode) => _statuses.Add(statusCode);

    // Human-readable leg outcomes (ok / expected failure) for the report.
    public void RecordEvent(string message) => _events.Add(message);

    public void RecordSnapshot(IntermediateSnapshot snapshot) => _snapshots.Add(snapshot);

    public IReadOnlyList<int> Statuses => [.. _statuses];

    public IReadOnlyList<string> Events => [.. _events];

    public IReadOnlyList<IntermediateSnapshot> Snapshots => [.. _snapshots.OrderBy(s => s.At)];
}
