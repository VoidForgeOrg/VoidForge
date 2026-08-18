namespace Voidforge.SoakTests;

// Everything the driver collected during the window: raw raced HTTP statuses (I4/I5), the ordered
// deposit series (I11), and the human-readable leg outcomes (report only).
public sealed record SoakDriverResult(
    IReadOnlyList<int> HttpStatuses,
    IReadOnlyList<IntermediateSnapshot> DepositSeries,
    IReadOnlyList<string> Events);
