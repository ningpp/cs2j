namespace CSharpToJava.Core.ClosureLift;

// M1: trigger classification only (used by analysis/statistics/diagnostics).
// M2 (Task 8) appends HolderGroupPlan / CapturedSymbolPlan / SiteRef records
// to this file — design §4.3 freezes their shape.
/// <summary>Why a symbol is lifted (design D2). Primary reason with deterministic precedence.</summary>
public enum TriggerReason
{
    ExternalWrite,
    InsideLambdaWrite,
    RefOutArgument,
    ForeachIteration,
}
