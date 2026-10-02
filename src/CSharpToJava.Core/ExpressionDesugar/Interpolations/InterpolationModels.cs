using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// The five handler-bindings (hard gate G0, design §4.3). Classification is
/// semantic-model/IOperation only — the <c>$</c> token text never participates.
/// </summary>
public enum InterpolationBindingKind
{
    /// <summary>Fully constant interpolation binding to string (IE-FOLD).</summary>
    Constant,

    /// <summary>Default System.String binding without a handler creation (IE-CONCAT/IE-FORMAT).</summary>
    DefaultString,

    /// <summary>Converts to System.FormattableString/IFormattable (IE-KEEP, CS2J0511).</summary>
    FormattableString,

    /// <summary>Custom [InterpolatedStringHandler] binding (G0: CS2J0510 Error/Keep).</summary>
    CustomHandler,

    /// <summary>Error type / undecidable binding (CS2J0512).</summary>
    Unknown,
}

/// <summary>The unsigned numeric kind of a hole's static type (empty for non-unsigned).</summary>
public enum UnsignedHoleKind
{
    None,
    Byte,
    UInt16,
    UInt32,
    UInt64,
}

/// <summary>Decided outcome path for one interpolation site.</summary>
public enum InterpolationPath
{
    /// <summary>Not yet decided.</summary>
    Pending,

    /// <summary>Replaced by one bare string literal (IE-FOLD).</summary>
    Fold,

    /// <summary>Replaced by string.Concat(...) (IE-CONCAT).</summary>
    Concat,

    /// <summary>Replaced by one string.Format(composite, h0, …) (IE-FORMAT).</summary>
    Format,

    /// <summary>Alignment clause is not a compile-time constant (CS2J0514; preserved).</summary>
    FormatNonConstantAlignment,

    /// <summary>FormattableString/IFormattable binding preserved (CS2J0511).</summary>
    KeptFormattableString,

    /// <summary>Inside an Expression&lt;T&gt; lambda, preserved per options (CS2J0515).</summary>
    SkippedExpressionTree,

    /// <summary>Custom handler: strict Error (file atomic) or Keep preserve (CS2J0510).</summary>
    CustomHandlerErrorOrKeep,

    /// <summary>Undecidable binding (CS2J0512).</summary>
    UnknownError,

    /// <summary>Non-materializable ref struct/pointer hole (CS2J0513).</summary>
    RefStructOrPointerError,
}

/// <summary>Per-hole semantic facts (design §4.3 HoleInfo).</summary>
public sealed class HoleInfo
{
    public required InterpolationSyntax Syntax { get; init; }
    public required ExpressionSyntax Expression { get; init; }

    /// <summary>Static converted type of the hole expression (SemanticModel truth).</summary>
    public ITypeSymbol? Type { get; init; }

    /// <summary>Compile-time alignment value (<c>,n</c>); null when absent/non-constant.</summary>
    public int? ConstantAlignment { get; init; }

    /// <summary>Format clause text (<c>:fmt</c>) decoded; null when absent.</summary>
    public string? FormatText { get; init; }

    public bool HasAlignmentClause { get; init; }
    public bool HasFormatClause { get; init; }

    /// <summary>True when the hole must route the whole site to the Format path.</summary>
    public bool NeedsFormatUpgrade { get; init; }

    /// <summary>Unsigned numeric classification (drives the ToString wrapping).</summary>
    public UnsignedHoleKind UnsignedKind { get; init; }

    /// <summary>True for byte/ushort/uint/ulong holes.</summary>
    public bool IsUnsignedNumeric => UnsignedKind != UnsignedHoleKind.None;

    /// <summary>ITypeSymbol.IsRefLikeType (cannot box into object).</summary>
    public bool IsRefStructLike { get; init; }

    /// <summary>TypeKind.Pointer / function pointer (cannot materialize).</summary>
    public bool IsPointer { get; init; }

    /// <summary>
    /// A ref-struct hole proven safe: the type itself overrides the
    /// parameterless ToString (emitted as x.ToString()).
    /// </summary>
    public bool HasSafeRefStructToString { get; init; }

    /// <summary>
    /// Safe for IE-CONCAT without a Format upgrade: primitive/enum/string
    /// (Q3 whitelist, nullable value forms unwrapped) or a non-IFormattable
    /// ordinary reference type; excludes ref-struct/pointer kinds handled on
    /// their own paths.
    /// </summary>
    public bool IsWhitelistConcatSafe { get; init; }
}

/// <summary>
/// One ordered content segment: either decoded literal text or a hole.
/// Text segments keep <c>ValueText</c> (Part A does no raw lexical work).
/// </summary>
public abstract class InterpolationSegment
{
    public sealed class Text(string valueText) : InterpolationSegment
    {
        public string ValueText { get; } = valueText;
    }

    public sealed class Hole(HoleInfo info) : InterpolationSegment
    {
        public HoleInfo Info { get; } = info;
    }
}

/// <summary>Full per-site plan produced by Analyze (design §4 IE-1).</summary>
public sealed class InterpolationPlan
{
    public required InterpolatedStringExpressionSyntax Node { get; init; }

    /// <summary>Assigned by Analyze after the expression-tree pre-check.</summary>
    public InterpolationBindingKind BindingKind { get; set; } = InterpolationBindingKind.Unknown;
    public InterpolationPath Path { get; set; } = InterpolationPath.Pending;

    /// <summary>Handler type display name (CS2J0510 message); null otherwise.</summary>
    public string? HandlerTypeName { get; set; }

    /// <summary>Folded constant value (IE-FOLD).</summary>
    public string? ConstantValue { get; set; }

    /// <summary>Ordered text/hole segments (ValueText decoded; holes zero-copied).</summary>
    public List<InterpolationSegment> Segments { get; } = new();

    /// <summary>Diagnostic codes reported for the site (residual pairing/audit).</summary>
    public List<string> DiagnosticCodes { get; } = new();

    public int Depth { get; init; }
}
