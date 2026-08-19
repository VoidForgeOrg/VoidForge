namespace Voidforge.SoakTests;

// The outcome of one Tier-3 check: its status plus a human-readable Detail carrying the numbers behind
// the verdict (shown in the report for both passes and failures, and reused for future Tier-2 blessing).
public sealed record OutcomeResult(string Id, string Title, OutcomeStatus Status, string Detail);
