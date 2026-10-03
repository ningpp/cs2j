namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Mutable accumulator (mirrors LocalFunctionLiftStatistics); merged across
/// files by the adapter. Buckets follow design §4.7.
/// </summary>
public sealed class ClosureLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    public int HolderClassesGenerated;
    public int HolderInstancesGenerated;

    /// <summary>Total captured symbols lifted; per-reason breakdown in <see cref="CapturesByReason"/>.</summary>
    public int CapturesLifted;
    public Dictionary<TriggerReason, int> CapturesByReason = new();

    public int SitesRewritten;
    public int ForeachPerIterationGroups;

    // ── C45 M1 (design §9 M1 row): named-method / delegate-site buckets. ──

    /// <summary>Zero-capture (incl. C# 9 static) lambdas lifted to host private static named methods (D10).</summary>
    public int StaticLambdaMethods;

    /// <summary>Total lambda methods lifted (M1 static + M2 instance `Lambda_&lt;k&gt;` methods).</summary>
    public int LambdaMethodsLifted;

    /// <summary>Lambda nodes replaced by explicit delegate creations `new DelType(Cs2jLambda_...)` (D8).</summary>
    public int DelegateSitesRewritten;

    /// <summary>Statement lambdas still present in the intermediate tree (quoted excluded; counted, not enforced in M1).</summary>
    public int ResidualStatementLambdas;

    // ── C45 M2 (design §9 M2 row): display-class buckets. ──

    /// <summary>
    /// Display classes generated. Same-value alias of <see cref="HolderClassesGenerated"/>
    /// for C32-only groups; grows further by the pure read-only/this groups the
    /// M2 consolidation creates (D3/D5/D7).
    /// </summary>
    public int DisplayClassesGenerated;

    /// <summary>Lambdas whose only captured state is `this` (D7).</summary>
    public int ThisOnlyLambdas;

    /// <summary>Display classes carrying a parent reference (D4; always 0 until M3).</summary>
    public int NestedDisplayClasses;

    /// <summary>Quoted Expression&lt;TDelegate&gt; lambdas skipped whole (D18; counted, not residual).</summary>
    public int ExpressionTreeLambdasSkipped;

    /// <summary>Statement lambdas deliberately kept (M3 buckets: discard parameters, nested capturing
    /// lambdas, local-function references, delegate-type inference positions…). Subset of
    /// <see cref="ResidualStatementLambdas"/>; anything beyond this bucket trips CS2J4510 (§7.3).</summary>
    public int AccountedResidualLambdas;

    /// <summary>CS2J3201-3206 rows keyed by code; residual gate rows counted separately.</summary>
    public Dictionary<string, int> SkippedByReason = new(StringComparer.Ordinal);

    /// <summary>Mutable captures still present in the re-parsed intermediate tree (must be 0 in M2 strict).</summary>
    public int ResidualMutableCaptures;

    public void AddSkipped(string code)
        => SkippedByReason[code] = SkippedByReason.TryGetValue(code, out var count) ? count + 1 : 1;
}
