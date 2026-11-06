namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Stable NORM construct ids shared by the residual registry, the pass contracts
/// and the normalization audit. These ids are the single source of truth: no
/// pass or registry entry should spell a NORM code as a literal string.
/// </summary>
public static class PassConstructIds
{
    public const string EnumArithmetic = "NORM-ENUM-ARITH";
    public const string StackAlloc = "NORM-STACKALLOC";
    public const string RefStruct = "NORM-REF-STRUCT";
    public const string RefLocal = "NORM-REF-LOCAL";
    public const string AsyncLambda = "NORM-ASYNC-LAMBDA";
    public const string TupleLocal = "NORM-TUPLE-LOCAL";
    public const string PointerOp = "NORM-POINTER-OP";
    public const string PointerLocal = "NORM-POINTER-LOCAL";
    public const string UnsafeBlock = "NORM-UNSAFE-BLOCK";
    public const string FixedStatement = "NORM-FIXED";

    /// <summary>
    /// 型变使用点未分类/未改写（VR pass 残差，方案 §6.4-R4/R2 NORM-GENERIC-*）。
    /// Owner: <c>variance-use-site-normalize</c>。
    /// </summary>
    public const string GenericVarianceUse = "NORM-GENERIC-VARIANCE-USE";
}
