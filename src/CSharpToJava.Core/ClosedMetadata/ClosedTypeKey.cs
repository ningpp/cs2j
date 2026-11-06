using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Normalized structural key of a closed type (design §5.1 ClosedTypeKey): the raw
/// symbol's fully-qualified display text plus recursively normalized type arguments,
/// with array-rank shape markers. Symbol equality aligns with
/// <see cref="SymbolEqualityComparer.Default"/> (which ignores nullable
/// annotations); the ordering text is a deterministic string so the record set is
/// sorted by it (CM-REG: never by enumeration order).
/// <para>
/// V-05 (upgrade plan §6.4-R0): nullable REFERENCE annotations are compile-time
/// only — they do not exist as runtime type information, and the Java-side
/// <c>closedCacheKey</c> is derived purely from runtime classes — so the key text
/// deliberately STRIPS the <c>?</c> annotation marker. Without the strip,
/// <c>List&lt;string&gt;</c> and <c>List&lt;string?&gt;</c> become two runtime
/// identities (two registrations for one type). Value-type nullability keeps its
/// distinction structurally: <c>int?</c> is the real generic closure
/// <c>Nullable&lt;int&gt;</c> and keys as such, distinct from <c>int</c>. NRT
/// metadata that RUNTIME reflection can observe (Yaml's NullableAttribute chain,
/// G-22) is a member-metadata concern, not a key concern — the key discards it,
/// the metadata path preserves it.
/// </para>
/// <para>
/// The text form is C#-local — structural alignment with the Java-side
/// <c>closedCacheKey</c> is the D3 contract, not a shared string.
/// </para>
/// </summary>
public sealed class ClosedTypeKey : IEquatable<ClosedTypeKey>, IComparable<ClosedTypeKey>
{
    public ITypeSymbol Type { get; }

    public string SortText { get; }

    private ClosedTypeKey(ITypeSymbol type, string sortText)
    {
        Type = type;
        SortText = sortText;
    }

    /// <summary>
    /// Creates the key for a closed type, or null when the shape has no structural key
    /// (bare type parameters, pointer/function-pointer shapes — callers report those
    /// with an explicit diagnostic instead of keying them).
    /// </summary>
    public static ClosedTypeKey? Create(ITypeSymbol type)
    {
        var text = BuildKeyText(type);
        return text is null ? null : new ClosedTypeKey(type, text);
    }

    public bool Equals(ClosedTypeKey? other)
        => other is not null
            && SortText.Equals(other.SortText, StringComparison.Ordinal)
            && SymbolEqualityComparer.Default.Equals(Type, other.Type);

    public override bool Equals(object? obj) => obj is ClosedTypeKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(SortText);

    public int CompareTo(ClosedTypeKey? other)
        => other is null ? 1 : string.Compare(SortText, other.SortText, StringComparison.Ordinal);

    public override string ToString() => SortText;

    private static string? BuildKeyText(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => null,
        IPointerTypeSymbol or IFunctionPointerTypeSymbol => null,
        IArrayTypeSymbol array => BuildKeyText(array.ElementType) is not { } element
            ? null
            : $"{element}[{array.Rank - 1}]",
        INamedTypeSymbol named when named.IsGenericType && !named.IsDefinition
            => JoinArguments(named.TypeArguments) is { } arguments
                ? named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                  + "(" + arguments + ")"
                : null,
        _ => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
    };

    private static string? JoinArguments(System.Collections.Immutable.ImmutableArray<ITypeSymbol> arguments)
    {
        var parts = new string[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var part = BuildKeyText(arguments[i]);
            if (part is null)
            {
                return null;
            }

            parts[i] = part;
        }

        return string.Join(",", parts);
    }
}
