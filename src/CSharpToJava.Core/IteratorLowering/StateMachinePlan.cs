namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// A variable (parameter or local) that must be hoisted to a state machine field
/// because its lifetime spans a yield suspension point.
/// </summary>
public sealed record CaptureField(
    string OriginalName,
    string Type,
    string FieldName);

/// <summary>
/// Complete lowering plan for one iterator member: field layout, state count,
/// protected regions, generated names. Produced by the analyzer, consumed by
/// the rewriter.
/// </summary>
public sealed record StateMachinePlan(
    string MemberName,
    string StateMachineClassName,
    string ElementType,
    bool IsIEnumerableReturn,
    bool IsIEnumeratorReturn,
    bool IsNonGenericReturn,
    bool IsGenericMethod,
    string DeclaringTypeName,
    string ThisCaptureType,
    bool IsStatic,
    IReadOnlyList<CaptureField> Parameters,
    IReadOnlyList<CaptureField> HoistedLocals,
    IReadOnlyList<CaptureField> HoistedEnumerators,
    IReadOnlyList<ControlFlow.RegionPlan> Regions,
    int YieldReturnCount,
    bool HasYieldBreak);
