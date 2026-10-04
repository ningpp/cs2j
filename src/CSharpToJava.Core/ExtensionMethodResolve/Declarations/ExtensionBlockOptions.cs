namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Policy for C#14 <c>extension</c> block materialization (C49 M1:
/// EMD-METHOD/EMD-PROP). The block form is a synthetic
/// <c>TypeKind.Extension</c> container; materialization rewrites it into
/// classic static-class members (block parent = host) and every receiver-style
/// call site into an explicit static call.
/// </summary>
public sealed record ExtensionBlockOptions
{
    /// <summary>
    /// When false (default), extension blocks keep the unconditional
    /// CS2J1206 syntax gate and the file is left untouched — the classic
    /// pass behavior is bit-for-bit unchanged. Only when true does C49
    /// analyze/materialize blocks (and take over the block gate from C19).
    /// </summary>
    public bool MaterializeExtensionBlocks { get; init; }

    /// <summary>EMD-METHOD: materialize instance extension methods as classic <c>this</c> static methods.</summary>
    public bool EnableMethods { get; init; } = true;

    /// <summary>EMD-PROP: materialize instance extension properties as <c>get_X</c>/<c>set_X</c> pairs.</summary>
    public bool EnableProperties { get; init; } = true;

    /// <summary>
    /// EMD-OP (M2): materialize block binary/unary/checked operators as
    /// ordinary host static methods and rewrite operator sites. Conversion
    /// operators are CS9282 inside blocks and are not in scope.
    /// </summary>
    public bool EnableOperators { get; init; } = true;

    /// <summary>
    /// EMD-STATIC (M2): materialize block static methods/properties as
    /// ordinary host static members and rewrite receiver-type-name qualified
    /// sites (with explicit block type arguments).
    /// </summary>
    public bool EnableStaticMembers { get; init; } = true;

    /// <summary>
    /// When true (default), enabled EMD-PROP/EMD-OP subrules require the
    /// member-desugar consumption chain downstream; without it the run is
    /// refused with CS2J1208. This flag is the policy half of the gate (the
    /// run-specific chain availability is the other half), exposed as a
    /// unit-testable decision seam.
    /// </summary>
    public bool RequireMemberDesugarChain { get; init; } = true;

    /// <summary>Strict mode: defer-classified sites are Errors instead of Warnings.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit plan/info diagnostics (default true).</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>
    /// How block member call sites inside <c>Expression&lt;TDelegate&gt;</c>
    /// trees are treated. M1 only supports <see cref="Skip"/>: such sites are
    /// paired diagnostics and the sugar stays (they anchor nowhere); a later
    /// milestone may add identity-asserted rewriting.
    /// </summary>
    public ExtensionBlockExpressionTreePolicy ExpressionTreePolicy { get; init; }
        = ExtensionBlockExpressionTreePolicy.Skip;
}

/// <summary>Expression-tree handling policy for block member sites (M1: skip only).</summary>
public enum ExtensionBlockExpressionTreePolicy
{
    /// <summary>Skip block sites inside expression trees with a paired diagnostic.</summary>
    Skip,
}
