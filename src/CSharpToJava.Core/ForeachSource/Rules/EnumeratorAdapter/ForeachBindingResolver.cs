using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C40 normalized foreach binding (design §4.2). Every classification field
/// comes straight from the <see cref="SemanticModel"/> — the resolver never
/// simulates C# candidate reduction itself. The Roslyn 4.12 surface has no
/// GetForEachSymbols API; <see cref="SemanticModel.GetForEachStatementInfo"/>
/// is the single source of truth.
/// </summary>
internal sealed class ForeachBinding
{
    /// <summary>RefKind of the iteration local symbol (None/Ref/RefReadOnly; authoritative).</summary>
    public required RefKind RefKind { get; init; }

    /// <summary>True when the collection expression is dynamically dispatched (Roslyn 5.0 has no ForEachStatementInfo.IsDynamicallyDispatched; see resolver note).</summary>
    public required bool IsDynamic { get; init; }

    /// <summary><c>ForEachStatementInfo.ElementType</c>.</summary>
    public ITypeSymbol? ElementType { get; init; }

    /// <summary>Static type of the collection expression (<c>GetTypeInfo(Expression).Type</c>).</summary>
    public ITypeSymbol? CollectionType { get; init; }

    // ── Enumerator pattern ──

    /// <summary>Compiler-selected GetEnumerator method (instance or extension); null for arrays/direct-enumerator/index-only patterns.</summary>
    public IMethodSymbol? GetEnumeratorMethod { get; init; }

    /// <summary>Compiler-selected MoveNext method; null when no enumerator binding exists.</summary>
    public IMethodSymbol? MoveNextMethod { get; init; }

    /// <summary>Compiler-selected Current property.</summary>
    public IPropertySymbol? CurrentProperty { get; init; }

    /// <summary>
    /// ForEachStatementInfo.DisposeMethod. Interpretation (M1 + M2 probes,
    /// see spec backfills 2026-10-01):
    /// <list type="bullet">
    /// <item>ref struct enumerator (<see cref="ITypeSymbol.IsRefLikeType"/>):
    /// this IS the authoritative pattern-dispose signal — non-null it is the
    /// accessible parameterless void Dispose() declared on the enumerator
    /// type itself and foreach emits a direct (unboxed) call; null means no
    /// finally. Ref structs cannot implement IDisposable.</item>
    /// <item>class/struct enumerator: NOT a decision signal — it reports
    /// IDisposable.Dispose for merely-convertible types (unsealed classes
    /// with a pattern-only Dispose()) and is null for plain structs with a
    /// public Dispose(). The builder decides via EnumeratorType/AllInterfaces
    /// and only cross-checks this symbol.</item>
    /// </list>
    /// </summary>
    public IMethodSymbol? DisposeMethod { get; init; }

    /// <summary>Concrete enumerator type: GetEnumerator return type, or the collection type for the direct-enumerator pattern.</summary>
    public ITypeSymbol? EnumeratorType { get; init; }

    /// <summary>
    /// Direct collection-is-enumerator pattern: GetEnumeratorMethod == null
    /// &amp;&amp; MoveNextMethod != null (decision 4). M1 probe: on Roslyn 5.0
    /// compilable C# never reaches this shape — foreach over a bare
    /// IEnumerator/duck-enumerator variable is CS1579 and the info returns
    /// all-empty symbols. Retained defensively; CS2J5452 is unreachable from
    /// valid input.
    /// </summary>
    public required bool DirectEnumerator { get; init; }

    // ── Index pattern (arrays / Length+indexer sources) ──

    /// <summary>FA-5 (M2): System.Span&lt;T&gt;/ReadOnlySpan&lt;T&gt; source with a resolved Length member and single-int indexer. FR-1/2 extend this in M3.</summary>
    public bool IsIndexPattern { get; init; }

    /// <summary>The Length/Count property or field used by the index loop (FA-5: Span.Length; M3 adds Count/custom sources).</summary>
    public ISymbol? LengthMember { get; init; }

    /// <summary>The <c>this[int]</c> indexer used by the index loop (may return by ref; RefKind is on the property symbol).</summary>
    public IPropertySymbol? Indexer { get; init; }
}

/// <summary>
/// Pure mapping from one <see cref="CommonForEachStatementSyntax"/> +
/// <see cref="SemanticModel"/> to a <see cref="ForeachBinding"/>. Detached
/// nodes / missing bindings yield null instead of throwing.
/// </summary>
internal static class ForeachBindingResolver
{
    public static ForeachBinding? Resolve(CommonForEachStatementSyntax node, SemanticModel model)
    {
        ForEachStatementInfo info;
        try
        {
            info = model.GetForEachStatementInfo(node);
        }
        catch (ArgumentException)
        {
            return null;
        }

        ITypeSymbol? collectionType = null;
        try
        {
            collectionType = model.GetTypeInfo(node.Expression).Type;
        }
        catch (ArgumentException)
        {
            // Detached expression: leave CollectionType null; enumerator symbols
            // below may still be present.
        }

        var refKind = RefKind.None;
        if (node is ForEachStatementSyntax plain)
        {
            if (model.GetDeclaredSymbol(plain) is ILocalSymbol local)
            {
                refKind = local.RefKind;
            }
        }

        var getEnumerator = info.GetEnumeratorMethod;
        var moveNext = info.MoveNextMethod;

        // NOTE (deviation from design §4.2): the referenced Roslyn surface is
        // 5.0.0 (not 4.12 as the design assumed); ForEachStatementInfo has no
        // IsDynamicallyDispatched member there. A foreach binds dynamically
        // exactly when its collection expression is dynamic (a statically
        // typed collection always reduces GetEnumerator statically), so the
        // collection TypeKind is the equivalent classification.
        var isDynamic = collectionType is { TypeKind: TypeKind.Dynamic };

        // Index-pattern classification (FA-5 M2 + FR-1/2 M3):
        //   * rank-1 arrays: IsIndexPattern with null LengthMember/Indexer —
        //     the builder renders the built-in `.Length` and `src[i]`;
        //   * System.Span<T>/ReadOnlySpan<T>: Length + this[int] symbols;
        //   * M3 custom sources: any type exposing a public Length/Count
        //     property or field plus a single-public-int indexer (the FR rule
        //     additionally checks the indexer's RefKind against the foreach's
        //     ref kind — a by-value indexer cannot back a ref foreach).
        // Multi-dimensional arrays are NOT an index pattern (FR-4 CS2J5485;
        // C# itself rejects ref foreach over them with CS1579).
        var isIndexPattern = false;
        ISymbol? lengthMember = null;
        IPropertySymbol? indexer = null;
        if (collectionType is IArrayTypeSymbol { Rank: 1 })
        {
            isIndexPattern = true;
        }
        else if (collectionType != null
                 && ForeachIndexLoopBuilder.TryFindIndexMembers(
                     collectionType, out lengthMember, out indexer))
        {
            isIndexPattern = true;
        }

        return new ForeachBinding
        {
            RefKind = refKind,
            IsDynamic = isDynamic,
            ElementType = info.ElementType,
            CollectionType = collectionType,
            GetEnumeratorMethod = getEnumerator,
            MoveNextMethod = moveNext,
            CurrentProperty = info.CurrentProperty,
            DisposeMethod = info.DisposeMethod,
            EnumeratorType = getEnumerator?.ReturnType ?? collectionType,
            DirectEnumerator = getEnumerator == null && moveNext != null,
            IsIndexPattern = isIndexPattern,
            LengthMember = lengthMember,
            Indexer = indexer,
        };
    }
}
