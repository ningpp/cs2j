using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// CE-SPREAD (design §2.3): classifies a spread source by its static symbol
/// type and, from M1, emits the single-evaluation AddRange / foreach-Add
/// statement sequence. The source expression appears exactly once and source
/// order is preserved; no System.Linq-based shapes are allowed.
/// M0 ships the symbol classification; the emission entry points are safe
/// empty placeholders (never invoked in M0).
/// </summary>
internal static class SpreadExpander
{
    /// <summary>
    /// Classifies a spread (<c>..e</c>) source. Order matters: array,
    /// Span/ReadOnlySpan, string (char elements), IEnumerable — otherwise
    /// unknown (CS2J0576).
    /// </summary>
    public static SpreadSourceKind ClassifySource(ITypeSymbol? sourceType)
    {
        if (sourceType == null
            || sourceType.TypeKind == TypeKind.Error
            || sourceType is IErrorTypeSymbol)
        {
            return SpreadSourceKind.Unknown;
        }

        if (sourceType is IArrayTypeSymbol)
            return SpreadSourceKind.Array;

        if (TargetTypeResolver.IsSpanLike(sourceType))
            return SpreadSourceKind.Span;

        if (sourceType.SpecialType == SpecialType.System_String)
            return SpreadSourceKind.StringChars;

        if (ImplementsNonGenericEnumerable(sourceType))
            return SpreadSourceKind.Enumerable;

        return SpreadSourceKind.Unknown;
    }

    /// <summary>
    /// Best-effort element type of a spread source: array/span/Span-like type
    /// argument, char for string, first IEnumerable&lt;T&gt; argument otherwise.
    /// </summary>
    public static ITypeSymbol? TryGetElementType(ITypeSymbol? sourceType)
    {
        if (sourceType is IArrayTypeSymbol array)
            return array.ElementType;

        if (sourceType.SpecialType == SpecialType.System_String)
            return null; // char — resolved by the compiler on foreach; no metadata name needed.

        if (sourceType is INamedTypeSymbol named && TargetTypeResolver.IsSpanLike(named)
            && named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0];
        }

        if (sourceType is INamedTypeSymbol genericEnumerable && genericEnumerable.IsGenericType)
        {
            var enumerableOfT = genericEnumerable.OriginalDefinition.ToDisplayString()
                is "System.Collections.Generic.IEnumerable<T>"
                ? genericEnumerable
                : null;
            enumerableOfT ??= FirstGenericEnumerable(genericEnumerable);
            if (enumerableOfT?.TypeArguments.Length == 1)
                return enumerableOfT.TypeArguments[0];
        }

        return sourceType?.AllInterfaces
            .Select(FirstGenericEnumerable)
            .FirstOrDefault(i => i != null)?.TypeArguments.FirstOrDefault();
    }

    // ---- M1 emission seam (not wired in M0; safe empty results) ----

    /// <summary>M1: emits AddRange(e) or the foreach-Add statement sequence.</summary>
    public static IReadOnlyList<StatementSyntax> BuildExpansionStatements(
        SpreadPlan plan,
        ExpressionSyntax tempTarget)
        => Array.Empty<StatementSyntax>();

    private static bool ImplementsNonGenericEnumerable(ITypeSymbol type)
        => type.SpecialType == SpecialType.System_Collections_IEnumerable
           || type.AllInterfaces.Any(i =>
               i.SpecialType == SpecialType.System_Collections_IEnumerable);

    private static INamedTypeSymbol? FirstGenericEnumerable(ITypeSymbol type)
        => type.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>");
}
