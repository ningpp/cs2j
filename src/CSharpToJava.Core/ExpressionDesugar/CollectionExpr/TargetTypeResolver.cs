using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Result of the M0 coarse target classification for one
/// <see cref="CollectionExpressionSyntax"/> site.
/// </summary>
internal sealed record TargetResolution(
    TargetKind Kind,
    ITypeSymbol? ConvertedType,
    bool IsMissingOrError,
    INamedTypeSymbol? BuilderType = null,
    string? BuilderFactoryMethod = null);

/// <summary>
/// Classifies a collection-expression site by
/// <c>SemanticModel.GetTypeInfo(node).ConvertedType</c> — the only allowed
/// source of truth (design hard invariant #1; source text is never used).
///
/// M0 implements T1 (array), T2 (Span/ReadOnlySpan by symbol full name),
/// T5 ([CollectionBuilder] metadata), interface detection and the error-type/
/// missing defense. Mutable-concrete ctor/Add verification (T3) and the
/// interface→concrete table (T4) land in M1/M2; other resolvable types return
/// <see cref="TargetKind.Unknown"/> for now.
/// </summary>
internal static class TargetTypeResolver
{
    public static TargetResolution Resolve(
        SemanticModel model,
        CollectionExpressionSyntax node)
    {
        var converted = model.GetTypeInfo(node).ConvertedType;

        if (IsMissingOrError(converted))
        {
            return new TargetResolution(
                TargetKind.Unknown, converted, IsMissingOrError: true);
        }

        // T1: array (rank-1 emitted M1; multidimensional reported via 0581).
        if (converted is IArrayTypeSymbol)
            return new TargetResolution(TargetKind.Array, converted, false);

        // T2: Span<T>/ReadOnlySpan<T> (symbol full name, never source text).
        if (IsSpanLike(converted))
            return new TargetResolution(TargetKind.Span, converted, false);

        if (converted is INamedTypeSymbol named)
        {
            // T5: [CollectionBuilder(typeof(B), "Create")].
            if (CollectionBuilderAnalyzer.TryGetBuilder(
                    named, out var builderType, out var factoryMethod))
            {
                return new TargetResolution(
                    TargetKind.CollectionBuilder, converted, false,
                    builderType, factoryMethod);
            }

            // T4 coarse bucket; the concrete-type table lands in M2.
            if (named.TypeKind == TypeKind.Interface)
                return new TargetResolution(TargetKind.Interface, converted, false);

            // T3 (concrete addable class/struct) needs ctor+Add binding checks
            // against IOperation; deferred to M1 — counted as Unknown in M0.
        }

        return new TargetResolution(TargetKind.Unknown, converted, false);
    }

    /// <summary>True for null, error-type symbols or the unresolved type marker.</summary>
    public static bool IsMissingOrError(ITypeSymbol? type)
        => type == null
           || type.TypeKind == TypeKind.Error
           || type is IErrorTypeSymbol;

    /// <summary>
    /// Symbol full-name check for System.Span&lt;T&gt; and
    /// System.ReadOnlySpan&lt;T&gt; (open original definition).
    /// </summary>
    public static bool IsSpanLike(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || !named.IsGenericType)
            return false;

        return named.OriginalDefinition.ToDisplayString() is
            "System.Span<T>" or "System.ReadOnlySpan<T>";
    }
}
