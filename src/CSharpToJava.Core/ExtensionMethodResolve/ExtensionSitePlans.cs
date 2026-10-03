using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>Kind of insertion anchor a temp-lifted site binds to.</summary>
internal enum ExtensionAnchorKind
{
    /// <summary>Enclosing statement (block statement or embedded statement wrapped in a block).</summary>
    Statement,

    /// <summary>Expression-bodied member (expanded to a block body).</summary>
    ArrowClause,

    /// <summary>Expression lambda (expanded to a block lambda; never used inside expression trees).</summary>
    ExpressionLambda,

    /// <summary>No statement anchor (field/property initializer, attribute, query clause, ...).</summary>
    None,
}

/// <summary>Resolved lift anchor for one conditional/group site.</summary>
/// <param name="Node">Anchor syntax node (statement/arrow/lambda) in the ORIGINAL tree.</param>
/// <param name="Kind">Anchor kind.</param>
/// <param name="ReturnsVoid">Whether the anchor context is void-valued (void member/lambda delegate).</param>
internal sealed record ExtensionAnchor(
    SyntaxNode Node,
    ExtensionAnchorKind Kind,
    bool ReturnsVoid)
{
    public static ExtensionAnchor None { get; } = new(null!, ExtensionAnchorKind.None, false);
}

/// <summary>One reduced extension call on a null-conditional chain segment.</summary>
/// <param name="Invocation">Origin/spine invocation (binds ReducedExtension).</param>
/// <param name="Reduced">Bound reduced symbol.</param>
/// <param name="Unreduced">Static Ordinary symbol (ReducedFrom).</param>
/// <param name="ReceiverRefKind">First parameter RefKind (EM-REF; a ref origin cannot be lifted).</param>
/// <param name="IsOrigin">True for the segment's member-binding invocation (receiver is the temp).</param>
internal sealed record ConditionalSpineCall(
    InvocationExpressionSyntax Invocation,
    IMethodSymbol Reduced,
    IMethodSymbol Unreduced,
    RefKind ReceiverRefKind,
    bool IsOrigin);

/// <summary>
/// One segment of a null-conditional chain: <c>r?.Go()</c> is one segment;
/// <c>r?.Go()?.Chain()</c> is two (the inner conditional's Expression holds
/// the outer segment's origin invocation).
/// </summary>
internal sealed record ExtensionConditionalSegment(
    ConditionalAccessExpressionSyntax Conditional,
    ExpressionSyntax Root,
    InvocationExpressionSyntax Origin,
    IReadOnlyList<ConditionalSpineCall> SpineCalls,
    ITypeSymbol? ResultType);

/// <summary>
/// EM-COND plan for one OUTERMOST null-conditional chain. Liftable chains
/// lower to single-evaluation temps + conditional static calls; non-liftable
/// shapes (no anchor, ref origin, foreign reduced calls, expression-tree
/// context) carry a skip code and the sugar stays with a diagnostic.
/// </summary>
internal sealed record ExtensionConditionalPlan
{
    public required ConditionalAccessExpressionSyntax OuterConditional { get; init; }

    public required IReadOnlyList<ExtensionConditionalSegment> Segments { get; init; }

    /// <summary>True when the chain result is discarded (void last call).</summary>
    public required bool IsVoid { get; init; }

    /// <summary>Result type of the outermost conditional (already nullable for value types); null when void.</summary>
    public required ITypeSymbol? ResultType { get; init; }

    public required bool Liftable { get; internal set; }

    /// <summary>CS2J1205 / internal reason when not liftable; null on liftable plans.</summary>
    public string? SkipCode { get; internal set; }

    /// <summary>Human-readable skip detail (diagnostics only).</summary>
    public string? SkipDetail { get; internal set; }

    public required ExtensionAnchor Anchor { get; init; }

    /// <summary>Receiver temp + intermediate segment temps (1 + Segments.Count - 1).</summary>
    public required IReadOnlyList<string> TempNames { get; init; }

    /// <summary>Annotation on the outermost conditional node (tracks it across rewrites).</summary>
    public required SyntaxAnnotation SiteAnnotation { get; init; }

    /// <summary>Annotation shared with the anchor node (TempLifter / post transforms).</summary>
    public required SyntaxAnnotation AnchorAnnotation { get; init; }

    /// <summary>Original span of the outer conditional (deterministic ordering).</summary>
    public int SpanStart => OuterConditional.SpanStart;
}

/// <summary>
/// EM-GROUP plan for one <c>r.M</c> non-call member access bound as a reduced
/// extension method group. Rewritable groups become capture lambdas
/// <c>(p...) => global::Host.M(r, p...)</c>; everything undecidable
/// (ambiguous/non-matching/no target type/no safe capture) carries CS2J1207.
/// </summary>
internal sealed record ExtensionGroupPlan
{
    public required MemberAccessExpressionSyntax MemberAccess { get; init; }

    public required IMethodSymbol Reduced { get; init; }

    public required IMethodSymbol Unreduced { get; init; }

    /// <summary>Delegate type the group converts to (Invoke signature drives the lambda shape).</summary>
    public required INamedTypeSymbol DelegateType { get; init; }

    public required bool IsInExpressionTree { get; init; }

    /// <summary>True when the receiver is a local/parameter/this reference usable directly inside the lambda.</summary>
    public required bool DirectCapture { get; init; }

    /// <summary>Null for direct capture; otherwise the resolved lift anchor.</summary>
    public required ExtensionAnchor? Anchor { get; init; }

    /// <summary>Receiver temp name (temp capture only).</summary>
    public string? TempName { get; init; }

    /// <summary>Generated lambda parameter names, in delegate Invoke order.</summary>
    public required IReadOnlyList<string> LambdaParameterNames { get; init; }

    /// <summary>Null when rewritable; CS2J1207 (or internal reason) otherwise.</summary>
    public string? SkipCode { get; init; }

    /// <summary>Human-readable skip detail (diagnostics only).</summary>
    public string? SkipDetail { get; init; }

    /// <summary>Anchor annotation (temp capture only).</summary>
    public SyntaxAnnotation? AnchorAnnotation { get; init; }

    public bool Rewritable => SkipCode is null;

    public int SpanStart => MemberAccess.SpanStart;
}

/// <summary>Why a residual sugar node is legitimately present after the rewrite (diagnostic-paired).</summary>
public enum ExtensionExemptKind
{
    /// <summary>EM-REF receiver could not be proven a writable lvalue (CS2J1204).</summary>
    RefReceiverMismatch,

    /// <summary>Null-conditional chain not liftable (CS2J1205).</summary>
    NullConditionalUnliftable,

    /// <summary>Null-conditional chain inside an expression tree: assert-only, never lifted.</summary>
    ExprTreeNullConditional,

    /// <summary>Reduced method group not rewritten (CS2J1207).</summary>
    ReducedMethodGroup,

    /// <summary>Expression-tree site rolled back to the sugar (CS2J1210).</summary>
    ExprTreeRollback,

    /// <summary>M4 EM-LINQ: user extension site on a chain mixed with LINQ-owned calls; chain kept sugar.</summary>
    MixedLinqChain,

    /// <summary>M4: host overridden to skip; the reduced site is intentionally left sugar.</summary>
    HostOverrideSkip,
}

/// <summary>
/// One diagnostic-paired residual shape, consumed by the post-rewrite
/// scanner gates 1/2/4. Matching is by file + compact node text (exempt nodes
/// are replayed byte-for-byte, so the text is identical) ordered by span.
/// </summary>
public sealed record ExtensionExemptSite(
    string FilePath,
    int SpanStart,
    ExtensionExemptKind Kind,
    string CompactText)
{
    /// <summary>Source span length for region exemptions (one chain covers several invocations).</summary>
    public int SpanLength { get; init; }
}
