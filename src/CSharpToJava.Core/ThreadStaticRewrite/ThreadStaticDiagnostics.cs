namespace CSharpToJava.Core.ThreadStaticRewrite;

public enum ThreadStaticSeverity { Info, Warning, Error }

public enum ThreadStaticRule
{
    Declaration,
    Read,
    Write,
    Compound,
    StructMutation,
    RefOut,
    ExpressionTree,
    Dynamic,
    GenericContainer,
    Instance,
    Residual,
    Rollback,
    Infrastructure,
}

public sealed record ThreadStaticDiagnostic(
    ThreadStaticSeverity Severity,
    ThreadStaticRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null,
    string? PlanId = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            ThreadStaticSeverity.Error => "error",
            ThreadStaticSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}:{Line?.ToString() ?? "?"}: {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>CS2J12xx diagnostic codes for the thread-static pass (spec §6; codes are append-only).</summary>
public static class ThreadStaticCodes
{
    public const string WholeTreeRollback = "CS2J1200";
    public const string UnsupportedAttributePosition = "CS2J1201";
    public const string InfeasibleSite = "CS2J1202";
    public const string StructMutationNotLiftable = "CS2J1203";
    public const string GenericContainerUnsupported = "CS2J1204";
    public const string ExpressionTreeSite = "CS2J1205";

    /// <summary>
    /// One field plan was excluded after the post-rewrite recompile attributed a
    /// new error to it; the plan was replayed from the original sources (M2).
    /// Appended to the §6 table (codes are append-only).
    /// </summary>
    public const string PlanRolledBack = "CS2J1206";

    public const string ResidualFound = "CS2J1220";
    public const string RestoreOrBuildFailed = "CS2J1230";
    public const string TestsFailed = "CS2J1231";
    public const string PlanNotSelfContained = "CS2J1290";
    public const string FieldRewritten = "TS001";
    public const string InitializerNormalized = "TS002";
}
