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

    /// <summary>Total lambda methods lifted (M1: equals StaticLambdaMethods; M2 adds instance methods).</summary>
    public int LambdaMethodsLifted;

    /// <summary>Lambda nodes replaced by explicit delegate creations `new DelType(Cs2jLambda_...)` (D8).</summary>
    public int DelegateSitesRewritten;

    /// <summary>Statement lambdas still present in the intermediate tree (quoted excluded; counted, not enforced in M1).</summary>
    public int ResidualStatementLambdas;

    /// <summary>CS2J3201-3206 rows keyed by code; residual gate rows counted separately.</summary>
    public Dictionary<string, int> SkippedByReason = new(StringComparer.Ordinal);

    /// <summary>Mutable captures still present in the re-parsed intermediate tree (must be 0 in M2 strict).</summary>
    public int ResidualMutableCaptures;

    public void AddSkipped(string code)
        => SkippedByReason[code] = SkippedByReason.TryGetValue(code, out var count) ? count + 1 : 1;
}
