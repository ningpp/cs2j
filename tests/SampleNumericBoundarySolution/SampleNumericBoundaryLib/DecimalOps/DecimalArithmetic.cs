namespace SampleNumericBoundaryLib.DecimalOps;

/// <summary>
/// C34 (decimal-ops) positive fixture: built-in decimal arithmetic, unary
/// sites and comparisons are reduced to explicit BCL static calls
/// (decimal.Add/Subtract/Multiply/Divide/Remainder/Negate/Compare).
/// Equality must go through decimal.Compare so different scales (1.0m vs
/// 1.00m) compare equal; the scale-sensitive instance equality surface would
/// report them different, so it must never be emitted.
/// </summary>
public static class DecimalArithmetic
{
    public static decimal Add(decimal a, decimal b) => a + b;

    public static decimal Subtract(decimal a, decimal b) => a - b;

    public static decimal Multiply(decimal a, decimal b) => a * b;

    public static decimal Divide(decimal a, decimal b) => a / b;

    public static decimal Remainder(decimal a, decimal b) => a % b;

    public static decimal Negate(decimal a) => -a;

    // Unary plus is an identity and is stripped by the rewrite.
    public static decimal PlusStripped(decimal a) => +a;

    public static bool EqualsOp(decimal a, decimal b) => a == b;

    public static bool NotEqualsOp(decimal a, decimal b) => a != b;

    public static bool LessThan(decimal a, decimal b) => a < b;

    public static bool LessThanOrEqual(decimal a, decimal b) => a <= b;

    public static bool GreaterThan(decimal a, decimal b) => a > b;

    public static bool GreaterThanOrEqual(decimal a, decimal b) => a >= b;

    /// <summary>Scale-sensitive path that must normalize through Compare.</summary>
    public static bool DifferentScalesCompareEqual() => 1.0m == 1.00m;

    public static decimal ChainedArithmetic(decimal a, decimal b, decimal c) =>
        (a + b) * c - (a / b);
}
