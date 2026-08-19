namespace Voidforge.SoakTests;

// How a blessed metric's tolerance is applied when comparing observed vs expected (the baseline JSON
// "kind"):
//   ExactIsh / Count -> absolute epsilon band: |observed - expected| <= tol
//   CountMin         -> at-least floor: observed >= expected (tol ignored)
//   Scalar           -> relative percent band: |observed - expected| <= expected * tol/100
public enum Tier2ToleranceKind
{
    ExactIsh,
    Count,
    CountMin,
    Scalar,
}
