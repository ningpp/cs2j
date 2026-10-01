using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Coarse target-type dispatch buckets (design §2.1 CE-TARGET table).
/// </summary>
public enum TargetKind
{
    /// <summary>T1: <c>T[]</c> (v1 rank-1; multidimensional is rejected 0581).</summary>
    Array,

    /// <summary>T2: System.Span&lt;T&gt; / System.ReadOnlySpan&lt;T&gt; (symbol full name, never text).</summary>
    Span,

    /// <summary>T3: concrete addable collection without [CollectionBuilder] (List&lt;T&gt;, ...).</summary>
    MutableConcrete,

    /// <summary>T4: interface target mapped to a specification concrete type (M2; M1 skips 0581).</summary>
    Interface,

    /// <summary>T5: type carrying [CollectionBuilder] (v1 rejects 0580; M2 emits the factory).</summary>
    CollectionBuilder,

    /// <summary>T6: error type, missing target, table-out type.</summary>
    Unknown,
}

/// <summary>
/// Static kind of a spread (<c>..e</c>) source expression (design §2.3 CE-SPREAD).
/// </summary>
public enum SpreadSourceKind
{
    /// <summary>Array source (AddRange-capable target temp).</summary>
    Array,

    /// <summary>System.Collections.Generic.IEnumerable&lt;T&gt; source (custom collections).</summary>
    Enumerable,

    /// <summary>string source (char elements; foreach-Add — string is not IEnumerable&lt;char&gt;).</summary>
    StringChars,

    /// <summary>Span&lt;T&gt;/ReadOnlySpan&lt;T&gt; source (ref struct; foreach expansion).</summary>
    Span,

    /// <summary>Not enumerable / error type / undecidable (CS2J0576).</summary>
    Unknown,
}

/// <summary>Element syntax kind covered by the rule (OfType-style filtering is forbidden).</summary>
public enum CollectionElementKind
{
    /// <summary><see cref="ExpressionElementSyntax"/> (ordinary element).</summary>
    Expression,

    /// <summary><see cref="SpreadElementSyntax"/> (<c>..e</c>).</summary>
    Spread,

    /// <summary>
    /// Any future/unknown <see cref="CollectionElementSyntax"/> kind. The rule
    /// has no silent-drop arm: such a kind is an explicit error (0581).
    /// </summary>
    Unsupported,
}

/// <summary>Decided lowering outcome for one site.</summary>
public enum CollectionExprOutcome
{
    /// <summary>Analyzed but not yet decided.</summary>
    Pending,

    /// <summary>new T[]{...} zero-temp array.</summary>
    ZeroTempArray,

    /// <summary>new C&lt;T&gt;(){...} zero-temp mutable collection.</summary>
    ZeroTempMutableCollection,

    /// <summary>__ed_n temp + Add/AddRange/foreach statement sequence (+ ToArray for array/Span targets).</summary>
    StatementSequence,

    /// <summary>Site intentionally kept, paired with a diagnostic.</summary>
    SkippedDiagnostic,
}

/// <summary>Spread-specific plan (source expression, its static kind and the element type).</summary>
public sealed class SpreadPlan
{
    public required SpreadElementSyntax Syntax { get; init; }
    public required ExpressionSyntax Source { get; init; }
    public SpreadSourceKind SourceKind { get; init; } = SpreadSourceKind.Unknown;

    /// <summary>Element type of the spread source (char for string; null when undecidable).</summary>
    public ITypeSymbol? ElementType { get; init; }
}

/// <summary>Plan for one element of a collection expression (every syntax kind covered).</summary>
public sealed class ElementPlan
{
    public required CollectionElementSyntax Syntax { get; init; }
    public required CollectionElementKind Kind { get; init; }
    public ExpressionSyntax? Expression { get; init; }
    public SpreadPlan? Spread { get; init; }

    /// <summary>
    /// The value type actually fed to the target (normal elements: the bound
    /// collection-element operation type, matching compiler-inserted
    /// conversions such as int-literal -> byte; spread elements: the source
    /// element type). Used for the T3 Add(P) compatibility check.
    /// </summary>
    public ITypeSymbol? FedType { get; init; }
}

/// <summary>Full per-site plan (design §4 CollectionExprModels) produced by Analyze.</summary>
public sealed class CollectionExprPlan
{
    public required CollectionExpressionSyntax Node { get; init; }
    public TargetKind TargetKind { get; init; } = TargetKind.Unknown;

    /// <summary>SemanticModel.GetTypeInfo(node).ConvertedType — the single source of truth.</summary>
    public ITypeSymbol? ConvertedType { get; init; }

    /// <summary>T5: builder type from the [CollectionBuilder] ctor argument.</summary>
    public INamedTypeSymbol? BuilderType { get; init; }

    /// <summary>T5: builder factory method name ("Create").</summary>
    public string? BuilderFactoryMethod { get; init; }

    /// <summary>Target element type symbol (array/Span element; Add(P) for concrete targets).</summary>
    public ITypeSymbol? TargetElementType { get; init; }

    /// <summary>T3: selected accessible single-parameter Add method (collection initializer pattern).</summary>
    public IMethodSymbol? AddMethod { get; init; }

    /// <summary>Element plans in source order (every CollectionElementSyntax represented).</summary>
    public List<ElementPlan> Elements { get; init; } = new();

    /// <summary>Nesting depth (0 = outermost collection expression).</summary>
    public int Depth { get; init; }

    /// <summary>True when the site contains at least one spread (statement lowering needed).</summary>
    public bool ContainsSpread { get; init; }

    /// <summary>True when a spread-bearing site was proven statement-liftable at its position.</summary>
    public bool Liftable { get; init; }

    public CollectionExprOutcome Outcome { get; set; } = CollectionExprOutcome.Pending;

    /// <summary>True for T1 arrays and T2 Span targets (both emit array forms in M1).</summary>
    public bool EmitsArrayForm =>
        TargetKind is TargetKind.Array or TargetKind.Span;
}
