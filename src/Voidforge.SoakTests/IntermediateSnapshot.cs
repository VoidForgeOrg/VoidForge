namespace Voidforge.SoakTests;

// A periodic during-run reading, evaluated at one fixed instant: every planet's remaining ore deposit
// (the ordered series feeds the I11 monotonicity check — deposits never rise) plus every building halt
// live at that instant (feeds Tier 3's O6 cascade check).
public sealed record IntermediateSnapshot(
    DateTimeOffset At,
    IReadOnlyDictionary<Guid, decimal> Deposits,
    IReadOnlyList<HaltObservation> Halts);
