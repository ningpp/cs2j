namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Per-category gray-release switches (design §4 CollectionExprOptions).
/// </summary>
public enum CollectionExprScope
{
    All,
    Array,
    MutableCollection,
    Interface,
    Spread,
    Guard,
}

/// <summary>
/// Policy for targets carrying <c>[CollectionBuilder]</c> (T5). v1 only knows
/// Error/Warning (diagnose and leave the site); M2 adds factory emission.
/// </summary>
public enum CollectionBuilderPolicy
{
    /// <summary>CS2J0580 Error under strict; the file is atomically not rewritten.</summary>
    Error,

    /// <summary>CS2J0580 Warning; the node is preserved.</summary>
    Warning,

    // M2: Emit — emit the compiler-bound builder factory call (reserved).
}

/// <summary>
/// Options for the C36 collection-expression rule (design §4).
/// </summary>
public sealed class CollectionExprOptions
{
    /// <summary>Error-class codes fail the file (atomically unchanged); false degrades them to warnings.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>Category gray-release scope (M1+; M0 analysis ignores the scope filter).</summary>
    public CollectionExprScope Scope { get; init; } = CollectionExprScope.All;

    /// <summary>Policy for [CollectionBuilder] targets (v1 reject; M2 emits).</summary>
    public CollectionBuilderPolicy Builder { get; init; } = CollectionBuilderPolicy.Error;

    /// <summary>T2: Span/ReadOnlySpan targets use the heap-array form (C06 owns the stack form).</summary>
    public bool RewriteSpanAsArray { get; init; } = true;

    /// <summary>CE-GUARD: run the C# 11/12 new-syntax inventory (M3).</summary>
    public bool EnableGuard { get; init; } = true;

    /// <summary>Default options instance.</summary>
    public static CollectionExprOptions Default { get; } = new();
}
