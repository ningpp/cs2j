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
}
