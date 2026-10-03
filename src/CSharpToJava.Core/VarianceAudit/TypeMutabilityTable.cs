using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Configurable mapping-mutability table (design D9). It records, for BCL read-only /
/// covariant interfaces, whether the Java type they map to exposes mutating members the
/// C# contract does not have. A Producer use of such a mapping is a "violating write
/// facade": Java code can mutate what C# declared read-only.
///
/// Seed data: <c>config/TypeMappings.json</c> does not carry BCL collection interfaces
/// (verified 2026-10-03 — no <c>IEnumerable</c>/<c>IReadOnly*</c> entry), so the seed is
/// derived from BCL common knowledge (the mapping the converter/compat layer emits for
/// these interfaces) and documented here. Types absent from the table that come from
/// metadata and declare variance are conservatively treated as "possibly expanding"
/// (only for interfaces — delegates map to functional interfaces with no mutating
/// members, and source-defined variant types keep their member set, design D9).
/// </summary>
public sealed class TypeMutabilityTable
{
    /// <summary>One mapped Java type plus the mutating members it exposes beyond the C# contract.</summary>
    public sealed record Entry(string JavaType, IReadOnlyList<string> MutatingMembers);

    /// <summary>Members name carried for unmapped metadata variant interfaces (conservative default).</summary>
    public static readonly IReadOnlyList<string> UnmappedMembers = new[] { "(unmapped)" };

    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private readonly Dictionary<string, Entry> _entries;

    /// <summary>Seed table with an optional override path (key = C# metadata name, e.g. <c>System.Collections.Generic.IReadOnlyList`1</c>).</summary>
    public TypeMutabilityTable(IEnumerable<KeyValuePair<string, Entry>>? overrides = null)
    {
        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var seed in Seeds())
        {
            _entries[seed.Key] = seed.Value;
        }

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                _entries[pair.Key] = pair.Value;
            }
        }
    }

    public Entry? Lookup(ITypeSymbol type)
        => type is INamedTypeSymbol named && TryGetMetadataKey(named.OriginalDefinition, out var key)
           && _entries.TryGetValue(key, out var entry)
            ? entry
            : null;

    /// <summary>
    /// D9 conservative default: metadata-declared variant <em>interface</em> absent from the
    /// table may have its mutability expanded by the mapping. Source-defined types and
    /// delegates are exempt (their member set is preserved / they are not containers).
    /// </summary>
    public bool IsConservativelyMutable(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        var original = named.OriginalDefinition;
        if (original.TypeKind != TypeKind.Interface)
        {
            return false;
        }

        if (original.Locations.Any(location => location.IsInSource))
        {
            return false;
        }

        if (!original.TypeParameters.Any(p => p.Variance != Microsoft.CodeAnalysis.VarianceKind.None))
        {
            return false;
        }

        return !TryGetMetadataKey(original, out var key) || !_entries.ContainsKey(key);
    }

    /// <summary>Builds the assembly-free metadata key, e.g. <c>System.Collections.Generic.IReadOnlyList`1</c>.</summary>
    public static bool TryGetMetadataKey(INamedTypeSymbol type, out string key)
    {
        var parts = new List<string>();
        for (var ns = type.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
        {
            parts.Insert(0, ns.Name);
        }

        if (parts.Count == 0)
        {
            key = string.Empty;
            return false;
        }

        parts.Add(type.MetadataName);
        key = string.Join('.', parts);
        return true;
    }

    private static IEnumerable<KeyValuePair<string, Entry>> Seeds()
    {
        yield return Seed("System.Collections.Generic.IEnumerable`1", "java.lang.Iterable", None);
        yield return Seed("System.Collections.Generic.IEnumerator`1", "java.util.Iterator", None);
        yield return Seed("System.Collections.Generic.IReadOnlyCollection`1", "java.util.List",
            new[] { "add", "set", "remove", "clear", "addAll", "removeAll" });
        yield return Seed("System.Collections.Generic.IReadOnlyList`1", "java.util.List",
            new[] { "add", "set", "remove", "clear", "addAll", "removeAll" });
        yield return Seed("System.Collections.Generic.IReadOnlyDictionary`2", "java.util.Map",
            new[] { "put", "remove", "clear", "putAll", "replace", "merge" });
        yield return Seed("System.Collections.Generic.IReadOnlySet`1", "java.util.Set",
            new[] { "add", "remove", "clear", "addAll", "removeAll", "retainAll" });
        yield return Seed("System.Collections.Generic.ICollection`1", "java.util.Collection",
            new[] { "add", "remove", "clear", "addAll", "removeAll", "retainAll" });
        yield return Seed("System.Collections.Generic.IList`1", "java.util.List",
            new[] { "add", "set", "remove", "clear", "addAll", "removeAll", "sort" });
        yield return Seed("System.Collections.Generic.IDictionary`2", "java.util.Map",
            new[] { "put", "remove", "clear", "putAll", "replace", "merge" });
        yield return Seed("System.Collections.Generic.ISet`1", "java.util.Set",
            new[] { "add", "remove", "clear", "addAll", "removeAll", "retainAll" });
    }

    private static KeyValuePair<string, Entry> Seed(string key, string javaType, IReadOnlyList<string> members)
        => new(key, new Entry(javaType, members));
}