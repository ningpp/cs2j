using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Coarse target-type dispatch buckets (design §2.1 CE-TARGET table).
/// </summary>
public enum TargetKind
{
    /// <summary>T1: <c>T[]</c> (v1 rank-1; multidimensional is rejected 0581 in M1/M2).</summary>
    Array,

    /// <summary>T2: System.Span&lt;T&gt; / System.ReadOnlySpan&lt;T&gt; (symbol full name, never text).</summary>
    Span,

    /// <summary>T3: concrete addable collection without [CollectionBuilder] (List&lt;T&gt;, ...).</summary>
    MutableConcrete,

    /// <summary>T4: interface target mapped to a specification concrete type (T5 table).</summary>
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
    /// <summary>System.Collections.Generic.IEnumerable&lt;T&gt; source (custom collections).</summary>
    Enumerable,

    /// <summary>Array source.</summary>
    Array,

    /// <summary>string source (char elements).</summary>
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
}

/// <summary>Decided lowering outcome for one site (populated from M1 on).</summary>
public enum CollectionExprOutcome
{
    /// <summary>M0: analyzed but no rewrite decision yet.</summary>
    Pending,

    /// <summary>new T[]{...} zero-temp array.</summary>
    ZeroTempArray,

    /// <summary>new C&lt;T&gt;(){...} zero-temp mutable collection / interface concrete type.</summary>
    ZeroTempMutableCollection,

    /// <summary>__ed_n temp + Add/AddRange/foreach statement sequence (+ ToArray for array targets).</summary>
    StatementSequence,

    /// <summary>Span target constructed as a heap array (0578).</summary>
    SpanAsArray,

    /// <summary>Site intentionally kept, paired with a diagnostic.</summary>
    SkippedDiagnostic,

    /// <summary>[CollectionBuilder] v1 rejection (0580).</summary>
    BuilderV1Reject,
}

/// <summary>Spread-specific plan (source expression, its static kind and the element type).</summary>
public sealed class SpreadPlan
{
    public required SpreadElementSyntax Syntax { get; init; }
    public required ExpressionSyntax Source { get; init; }
    public SpreadSourceKind SourceKind { get; init; } = SpreadSourceKind.Unknown;
    public ITypeSymbol? ElementType { get; init; }

    /// <summary>Chosen expansion: AddRange / span-foreach / enumerable-foreach (M1).</summary>
    public string? Expansion { get; init; }
}

/// <summary>Plan for one element of a collection expression (every syntax kind covered).</summary>
public sealed class ElementPlan
{
    public required CollectionElementSyntax Syntax { get; init; }
    public required CollectionElementKind Kind { get; init; }
    public ExpressionSyntax? Expression { get; init; }
    public SpreadPlan? Spread { get; init; }
}

/// <summary>
/// Full per-site plan (design §4 CollectionExprModels). M0 fills only the
/// node/target/type/counting fields; emission and statement-lift fields are
/// consumed from M1 on.
/// </summary>
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

    /// <summary>T4: selected specification concrete type (List&lt;T&gt;/Dictionary&lt;,&gt;/List&lt;object?&gt;).</summary>
    public INamedTypeSymbol? SelectedConcreteType { get; init; }

    /// <summary>Element plans in source order (every CollectionElementSyntax represented).</summary>
    public List<ElementPlan> Elements { get; init; } = new();

    /// <summary>Nesting depth (0 = outermost collection expression).</summary>
    public int Depth { get; init; }

    /// <summary>True when the site contains at least one spread (statement lowering needed).</summary>
    public bool ContainsSpread { get; init; }

    /// <summary>True when the site is in a position where statements cannot be lifted.</summary>
    public bool RequiresStatementLift { get; init; }

    public CollectionExprOutcome Outcome { get; set; } = CollectionExprOutcome.Pending;
}
