namespace Voidforge.SoakTests;

// A periodic during-run reading of every planet's remaining ore deposit, evaluated at one fixed
// instant. The ordered series feeds the I11 monotonicity check (deposits never rise).
public sealed record IntermediateSnapshot(DateTimeOffset At, IReadOnlyDictionary<Guid, decimal> Deposits);
