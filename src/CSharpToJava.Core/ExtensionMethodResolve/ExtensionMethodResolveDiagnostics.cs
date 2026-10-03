namespace CSharpToJava.Core.ExtensionMethodResolve;

public enum ExtensionSeverity { Info, Warning, Error }

/// <summary>Rule owning a diagnostic (EM-x set, design §4.3).</summary>
public enum ExtensionRule
{
    /// <summary>EM-BIND/EM-CALL: reduced binding and core rewrite.</summary>
    Bind,

    /// <summary>EM-GEN/EM-GEN-HOST: method/host generic type arguments.</summary>
    Gen,

    /// <summary>EM-REF: this ref/in receiver argument modifiers.</summary>
    Ref,

    /// <summary>EM-COND: null-conditional extension calls.</summary>
    Cond,

    /// <summary>EM-GROUP: reduced extension method groups (non-call member access).</summary>
    Group,

    /// <summary>EM-EXT14: C# 14 extension blocks / extension properties (owned by C49 later).</summary>
    Ext14,

    /// <summary>EM-LINQ: LINQ host ownership and mixed chains.</summary>
    Linq,

    /// <summary>EM-EXTERNAL: metadata hosts and Java-mapping grading.</summary>
    External,

    /// <summary>EM-EXPRTREE: expression-tree identity assertion.</summary>
    ExprTree,

    /// <summary>Post-rewrite residual gate (design §4.5).</summary>
    Residual,

    /// <summary>Intermediate-tree restore/build/test infrastructure.</summary>
    Infra,
}

public sealed record ExtensionDiagnostic(
    ExtensionSeverity Severity,
    ExtensionRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            ExtensionSeverity.Error => "error",
            ExtensionSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}({Line?.ToString() ?? "?"}): {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>CS2J12xx / EM001 diagnostic codes for the extension-methods pass (design §6).</summary>
public static class ExtensionMethodCodes
{
    /// <summary>New whole-tree validation error unattributable to one file → full run rollback.</summary>
    public const string WholeTreeRollback = "CS2J1200";

    /// <summary>Ambiguous extension resolution (CandidateReason.Ambiguous); site left unchanged.</summary>
    public const string Ambiguous = "CS2J1201";

    /// <summary>Extension-like sugar unbound (None/OverloadResolutionFailure/incomplete references); site unchanged.</summary>
    public const string Unbound = "CS2J1202";

    /// <summary>Metadata host without a Java landing mapping (CS2J_EXT001 successor at preprocess time).</summary>
    public const string ExternalHostNoMapping = "CS2J1203";

    /// <summary>Defensive: ref/in modifier cannot be proven semantically compatible with the argument.</summary>
    public const string RefMismatch = "CS2J1204";

    /// <summary>Null-conditional extension call r?.M() not lifted by v1 (only for shapes the probe proves un-liftable).</summary>
    public const string NullConditional = "CS2J1205";

    /// <summary>C# 14 extension block / extension property / extension operator (classic form之外); always Error; points to C49/C52.</summary>
    public const string CSharp14ExtensionBlock = "CS2J1206";

    /// <summary>Reduced extension method group (non-call member access) not rewritten in v1.</summary>
    public const string ReducedMethodGroup = "CS2J1207";

    /// <summary>Expression-tree identity assertion failed; the single site rolls back to the sugar.</summary>
    public const string ExpressionTreeIdentityFailed = "CS2J1210";

    /// <summary>Residual gate found a remaining shape above whitelist (design §4.5).</summary>
    public const string ResidualFound = "CS2J1220";

    /// <summary>Intermediate-tree restore/build failed.</summary>
    public const string RestoreOrBuildFailed = "CS2J1230";

    /// <summary>Intermediate-tree tests failed.</summary>
    public const string TestsFailed = "CS2J1231";

    /// <summary>Single-file mode: binding incomplete due to missing references; conservatively skipped.</summary>
    public const string SingleFileIncompleteBinding = "CS2J1290";

    /// <summary>Per-site detail (host full name, classification, chain/generic/ref flags); verbose only.</summary>
    public const string Info = "EM001";
}
