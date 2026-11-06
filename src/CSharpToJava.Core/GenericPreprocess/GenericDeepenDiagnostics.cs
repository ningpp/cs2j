namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericDeepenSeverity { Info, Warning, Error }

public enum GenericDeepenRule { Anchor, IsAs, Catch, StaticMember, StaticField, Nested, Residual, Infrastructure }

public sealed record GenericDeepenDiagnostic(
    GenericDeepenSeverity Severity,
    GenericDeepenRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            GenericDeepenSeverity.Error => "error",
            GenericDeepenSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}:{Line?.ToString() ?? "?"}: {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>CS2J11xx diagnostic codes for the generic-deepen pass (spec §6).</summary>
public static class GenericDeepenCodes
{
    public const string WholeTreeRollback = "CS2J1100";
    public const string UndecidableSite = "CS2J1101";
    public const string StaticFamilyNotClosable = "CS2J1102";
    public const string StaticFieldNotRewritable = "CS2J1103";
    public const string PatternNotLiftable = "CS2J1104";
    public const string CatchLoweringPreconditionFailed = "CS2J1105";
    public const string CallSiteOutsideCompilation = "CS2J1106";

    /// <summary>
    /// V-04 (upgrade plan §6.4-R0): a bodiless (abstract/interface) family
    /// member needs a token anchor, but materializing a stub body would
    /// mutate the public ABI (abstract→virtual, interface DIM with a
    /// throwing body). The member is left VERBATIM and the degradation is
    /// REGISTERED with this Warning (never escalated by strict mode — it is
    /// a declared limitation, not a correctness failure).
    /// </summary>
    public const string AnchorMemberNotMaterializable = "CS2J1107";

    /// <summary>
    /// V-07 (upgrade plan §6.4-R0/GD-ISAS-EXPR): an <c>as T</c> cast to a type
    /// parameter whose receiver cannot be re-evaluated without observable side
    /// effects (method call, property, indexer, ...). The canonical lowering
    /// <c>typeof(T).IsInstanceOfType(x) ? (T)(object)x : default</c> would evaluate
    /// the receiver twice, so the expression is left VERBATIM and the degradation
    /// is REGISTERED here — the Java side has no type-parameter branch for
    /// <c>as</c>, so strict (java-ready) escalates this Warning to Error instead of
    /// emitting an illegal <c>instanceof T</c>.
    /// </summary>
    public const string AsCastReceiverNotRepeatable = "CS2J1108";

    public const string ExpressionTreeSite = "CS2J1110";
    public const string ResidualFound = "CS2J1120";
    public const string RestoreOrBuildFailed = "CS2J1130";
    public const string TestsFailed = "CS2J1131";
    public const string PlanNotSelfContained = "CS2J1190";
    public const string Info = "GD001";
}
