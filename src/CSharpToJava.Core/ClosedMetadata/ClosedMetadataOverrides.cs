using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Explicit skip/force overrides for the closed-metadata residual gate
/// (design §6.3): entries are fully-qualified type or member names only — never
/// positional patterns. JSON form:
/// <code>
/// {
///   "skip":  ["global::Some.App.Runner", "global::System.Collections.Generic.List&lt;&gt;"],
///   "force": ["global::Some.App.RegisteredElsewhere"]
/// }
/// </code>
/// A skip entry declares the site out of the A1 served set (the gate stays
/// silent); a force entry asserts out-of-band registration exists (A2/manual).
/// Both suppress findings and both leave a CLO001 Info trace; neither unskips a
/// CM-DYN dynamic site (design §7: force does not lift CS2J1210).
/// </summary>
public sealed class ClosedMetadataOverrides
{
    public static ClosedMetadataOverrides Empty { get; } = new();

    private readonly HashSet<string> _skip;
    private readonly HashSet<string> _force;

    public ClosedMetadataOverrides(
        IEnumerable<string>? skip = null,
        IEnumerable<string>? force = null)
    {
        _skip = Normalize(skip);
        _force = Normalize(force);
    }

    public IReadOnlyCollection<string> SkipEntries => _skip;
    public IReadOnlyCollection<string> ForceEntries => _force;

    /// <summary>
    /// Loads the JSON overrides file. Throws <see cref="ClosedMetadataOverridesException"/>
    /// on malformed JSON or non-string/non-array entries; the CLI adapter reports the
    /// load failure and runs the gate without overrides.
    /// </summary>
    public static ClosedMetadataOverrides Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ClosedMetadataOverridesException("the overrides root must be a JSON object.");
        }

        return new ClosedMetadataOverrides(
            ReadArray(document.RootElement, "skip"),
            ReadArray(document.RootElement, "force"));
    }

    /// <summary>
    /// Whether a gate finding keyed by <paramref name="member"/> (the enclosing
    /// serializer entry, when present) and the closed types it consumes is
    /// explicitly overridden. <paramref name="force"/> reports whether the
    /// matching entry is a force assertion (vs a skip).
    /// </summary>
    public bool IsSuppressed(
        ISymbol? member,
        IReadOnlyList<ITypeSymbol>? types,
        out string? matchedEntry,
        out bool force)
    {
        if (TryMatchMember(member, out matchedEntry)
            || types is not null && TryMatchType(types, out matchedEntry))
        {
            force = matchedEntry is not null && _force.Contains(matchedEntry);
            return true;
        }

        force = false;
        return false;
    }

    private bool TryMatchMember(ISymbol? member, out string? matchedEntry)
    {
        matchedEntry = null;
        if (member is null)
        {
            return false;
        }

        var original = member.OriginalDefinition;
        var candidates = new List<string>
        {
            NormalizeText(original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
        };
        if (original.ContainingType is { } containing)
        {
            candidates.Add(NormalizeText(
                containing.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                + "." + original.Name));
        }

        foreach (var candidate in candidates)
        {
            if (_skip.Contains(candidate) || _force.Contains(candidate))
            {
                matchedEntry = candidate;
                return true;
            }
        }

        return false;
    }

    private static readonly SymbolDisplayFormat OpenGenericFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

    private bool TryMatchType(IReadOnlyList<ITypeSymbol> types, out string? matchedEntry)
    {
        foreach (var type in types)
        {
            foreach (var closed in Flatten(type))
            {
                if (closed is not INamedTypeSymbol named)
                {
                    continue;
                }

                var exact = NormalizeText(named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (_skip.Contains(exact) || _force.Contains(exact))
                {
                    matchedEntry = exact;
                    return true;
                }

                // Open form covers every closed construction; accept both the
                // Roslyn no-parameter rendering "Namespace.Type" and the spelled
                // "Namespace.Type<>" form.
                if (named.IsGenericType)
                {
                    var open = NormalizeText(named.OriginalDefinition.ToDisplayString(OpenGenericFormat));
                    var openWithBrackets = open + "<>";
                    if (_skip.Contains(open) || _force.Contains(open)
                        || _skip.Contains(openWithBrackets) || _force.Contains(openWithBrackets))
                    {
                        matchedEntry = openWithBrackets;
                        return true;
                    }
                }
            }
        }

        matchedEntry = null;
        return false;
    }

    private static IEnumerable<ITypeSymbol> Flatten(ITypeSymbol type)
    {
        yield return type;
        if (type is INamedTypeSymbol { IsDefinition: false } named)
        {
            foreach (var argument in named.TypeArguments)
            {
                foreach (var nested in Flatten(argument))
                {
                    yield return nested;
                }
            }
        }
        else if (type is IArrayTypeSymbol array)
        {
            foreach (var nested in Flatten(array.ElementType))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<string>? ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ClosedMetadataOverridesException($"property '{name}' must be a JSON array of strings.");
        }

        var entries = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ClosedMetadataOverridesException($"property '{name}' must contain strings only.");
            }

            entries.Add(item.GetString()!);
        }

        return entries;
    }

    private static HashSet<string> Normalize(IEnumerable<string>? entries)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (entries is null)
        {
            return set;
        }

        foreach (var entry in entries)
        {
            var normalized = NormalizeText(entry);
            if (normalized.Length > 0)
            {
                set.Add(normalized);
            }
        }

        return set;
    }

    private static string NormalizeText(string text)
    {
        var trimmed = text.Trim();
        const string globalPrefix = "global::";
        return trimmed.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? trimmed[globalPrefix.Length..]
            : trimmed;
    }
}

/// <summary>Raised when a closed-metadata overrides file cannot be parsed.</summary>
public sealed class ClosedMetadataOverridesException : Exception
{
    public ClosedMetadataOverridesException(string message)
        : base(message)
    {
    }
}
