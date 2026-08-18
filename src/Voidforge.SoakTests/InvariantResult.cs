namespace Voidforge.SoakTests;

// The outcome of one Tier-1 invariant: an empty Violations list means it held.
public sealed record InvariantResult(string Id, string Title, IReadOnlyList<string> Violations)
{
    public bool Passed => Violations.Count == 0;
}
