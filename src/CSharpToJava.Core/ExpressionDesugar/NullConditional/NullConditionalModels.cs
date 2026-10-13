using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>How a chain site is consumed (design §4.3 shapes).</summary>
internal enum NullConditionalContextKind
{
    /// <summary>Value-producing expression position.</summary>
    Value,

    /// <summary>Expression of an <see cref="ExpressionStatementSyntax"/> (incl. discarded value call).</summary>
    VoidStatement,
}

/// <summary>Kind of the innermost WhenNotNull terminal of a chain.</summary>
internal enum ChainTerminalKind
{
    /// <summary>Member value: a binding-rooted member-access chain without invocation.</summary>
    Member,

    /// <summary>Conditional ordinary method call (receiver not a delegate).</summary>
    Call,

    /// <summary>Conditional index <c>?[]</c>.</summary>
    Index,

    /// <summary>Delegate/event <c>?.Invoke(...)</c>.</summary>
    Invoke,

    /// <summary>WhenNotNull is an assignment expression — owned by C51.</summary>
    Assign,

    /// <summary>Unrecognized terminal syntax (CS2J0564).</summary>
    Unknown,
}

/// <summary>Lowering shape selected for a chain (design §4.1 Shape).</summary>
internal enum NullConditionalLoweringShape
{
    /// <summary>Zero-declaration inline ternary, whitelist receiver.</summary>
    Inline,

    /// <summary>One segment temp + ternary (single segment, non-whitelist receiver).</summary>
    TempTernary,

    /// <summary>Segment temps with guarded prefetches + nested ternary (multi segment, value).</summary>
    GuardedBlock,

    /// <summary>Segment temps + nested if blocks (void statement).</summary>
    StatementIf,
}

/// <summary>What the rewriter does with a planned chain.</summary>
internal enum ChainOutcome
{
    /// <summary>Rewritten into ordinary C# expressions/statements.</summary>
    Lowered,

    /// <summary>Preserved with the explained annotation (skip/defer/C51/non-strict).</summary>
    Preserved,
}

/// <summary>
/// One segment of a chain: corresponds to one <c>?.</c>/<c>?[]</c>
/// <see cref="ConditionalAccessExpressionSyntax"/>. Segment 0 owns the outermost
/// receiver expression; segment k (k≥1) owns the inner CA's Expression, a
/// binding-rooted expression referring to segment k-1.
/// </summary>
internal sealed class ChainSegment
{
    /// <summary>Original CA node this segment belongs to.</summary>
    public required ConditionalAccessExpressionSyntax Ca { get; init; }

    /// <summary>Original <c>ca.Expression</c> receiver of this segment.</summary>
    public required ExpressionSyntax ReceiverOriginal { get; init; }

    /// <summary>
    /// Static non-null receiver type of the segment (segment k: type of the
    /// inner binding-rooted expression). Segment 0 may be reported nullable;
    /// segment temps use <c>var</c> so this is informational for seg 0.
    /// </summary>
    public required ITypeSymbol? ReceiverType { get; init; }

    /// <summary>
    /// Allocated <c>__ed_sN</c> segment temp name. Allocated lazily (after the
    /// decision walk) only for lowered shapes that need temps; empty for
    /// inline/whitelist segments.
    /// </summary>
    public string TempName { get; set; } = string.Empty;
}

/// <summary>The innermost WhenNotNull terminal.</summary>
internal sealed class ChainTerminal
{
    public required ChainTerminalKind Kind { get; init; }

    /// <summary>Original innermost <c>WhenNotNull</c> syntax.</summary>
    public required ExpressionSyntax ExpressionOriginal { get; init; }

    /// <summary>True when an Invoke terminal binds to an event symbol (field-style event).</summary>
    public required bool IsEvent { get; init; }
}

/// <summary>
/// The whole-chain plan for one outermost <see cref="ConditionalAccessExpressionSyntax"/>
/// (design §4.1). Built once during Analyze against the original tree; the
/// rewriter matches original root nodes and renders using visited children.
/// </summary>
internal sealed class ChainPlan
{
    /// <summary>Original outermost CA node (rewriter key).</summary>
    public required ConditionalAccessExpressionSyntax RootOriginal { get; init; }

    public required List<ChainSegment> Segments { get; init; }

    public required ChainTerminal Terminal { get; init; }

    public required NullConditionalContextKind ContextKind { get; init; }

    // Shape / Outcome / SkipCode are assigned by the analyzer's decision walk
    // after construction, so they use ordinary mutable setters.
    public NullConditionalLoweringShape Shape { get; set; }

    /// <summary>Statement/arrow anchor found by FindAnchor; null in anchor-free contexts.</summary>
    public required SyntaxNode? Anchor { get; init; }

    /// <summary>Chain result type: <c>GetTypeInfo(root).Type</c> (lifted T? for value terminals).</summary>
    public required ITypeSymbol? ResultType { get; init; }

    /// <summary>Terminal value type on the non-null path.</summary>
    public required ITypeSymbol? TerminalValueType { get; init; }

    /// <summary>True when the true/null branches need an explicit <c>(T?)</c> cast.</summary>
    public required bool CastToNullable { get; init; }

    /// <summary>Rendered nullable cast type (the result type), when <see cref="CastToNullable"/>.</summary>
    public required TypeSyntax? NullableCastType { get; init; }

    public ChainOutcome Outcome { get; set; }

    /// <summary>Diagnostic code explaining a preserved site (skip/defer); null when lowered.</summary>
    public string? SkipCode { get; set; }

    /// <summary>
    /// True for a value chain that had no statement anchor at analyze time but
    /// may acquire one during the family lift (e.g. an initializer site inside
    /// a field/property initializer is moved into a synthesized builder
    /// method). The primary rewriter leaves such sites untouched; the post-lift
    /// late pass lowers them against their NEW hosting statement. Sites still
    /// anchor-free after the lift are re-diagnosed as CS2J0557 (strict Error).
    /// </summary>
    public bool AnchorDeferred { get; set; }

    /// <summary>True when the site sits inside an E03 (<c>___tN</c>) temp initializer (NC-6).</summary>
    public required bool IsCoalesceComposition { get; init; }

    /// <summary>NA-6: terminal operator is <c>??=</c> inside the conditional access.</summary>
    public bool IsCoalesceAssignment { get; set; }

    /// <summary>NA-6: inner coalesce-temp name <c>__ed_nN</c> (empty for other shapes).</summary>
    public string InnerTempName { get; set; } = string.Empty;

    public int Length => Segments.Count;
}
