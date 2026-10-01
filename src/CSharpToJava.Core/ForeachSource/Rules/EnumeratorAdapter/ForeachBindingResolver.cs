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
    /// ForEachStatementInfo.DisposeMethod, kept for cross-checking only. M1
    /// probes proved it is NOT a disposal decision signal on Roslyn 5.0: it
    /// reports IDisposable.Dispose for merely-convertible types (non-sealed
    /// classes, pattern-only Dispose()) and is null on structs. The builder
    /// classifies disposal from EnumeratorType/AllInterfaces instead.
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

    // ── Index pattern (arrays / Length+indexer sources); declared now, filled in M3 ──

    /// <summary>M3 (FA-5/FR-1/2): array or Length/Count + single-int-argument indexer source. Always false in M1.</summary>
    public bool IsIndexPattern { get; init; }

    /// <summary>M3: the Length/Count property or field used by the index loop. Null in M1.</summary>
    public ISymbol? LengthMember { get; init; }

    /// <summary>M3: the <c>this[int]</c> indexer used by the index loop. Null in M1.</summary>
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
            IsIndexPattern = false,
            LengthMember = null,
            Indexer = null,
        };
    }
}
