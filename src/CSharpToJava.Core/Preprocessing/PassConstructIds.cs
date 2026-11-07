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

    /// <summary>
    /// 约束不可表达/改写无法安全证明的泛型约束站点（R2/CN 残差，方案 §6.3
    /// NORM-GENERIC-CONSTRAINT）。Owner: <c>generic-constraint</c>。
    /// </summary>
    public const string GenericConstraint = "NORM-GENERIC-CONSTRAINT";

    /// <summary>
    /// 最终树语义闭包残差（R13，方案 §6.4-R13）：构造枚举后没有已启用且成功的
    /// owner pass 接管的残留（或 finalizer 声明本身非法）。本首版只登记，不改变
    /// 链行为；owner 能力仍由 finalizer/后续 VS 切片接管。
    /// </summary>
    public const string SemanticClosure = "NORM-SEMANTIC-CLOSURE";
}
