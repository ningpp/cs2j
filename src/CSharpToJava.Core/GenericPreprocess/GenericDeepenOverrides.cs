using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// Explicit skip/force overrides for the generic-deepen residual gate
/// (spec §6): entries are fully-qualified method/type/field names only — never
/// positional patterns. JSON form (same shape as the closed-metadata gate):
/// <code>
/// {
///   "skip":  ["global::Some.App.Sample", "global::Some.App.Sample.F&lt;T&gt;"],
///   "force": ["global::Some.App.ManuallyAnchored"]
/// }
/// </code>
/// A skip entry declares the residual site out of scope (the gate stays silent);
/// a force entry asserts the token is supplied out of band (e.g. a later L2 tail
/// parameter). Both suppress the residual finding and both leave a GD001 Info
/// trace. Neither lifts a CS2J1110 expression-tree finding (force does not unlock
/// tree rewrites).
/// </summary>
public sealed class GenericDeepenOverrides
{
    public static GenericDeepenOverrides Empty { get; } = new();

    private readonly HashSet<string> _entries;
    private readonly HashSet<string> _force;

    public GenericDeepenOverrides(
        IEnumerable<string>? skip = null,
        IEnumerable<string>? force = null)
    {
        _entries = Normalize(skip);
        _force = new HashSet<string>(StringComparer.Ordinal);
        if (force is not null)
        {
            foreach (var entry in force)
            {
                var normalized = NormalizeText(entry);
                if (normalized.Length > 0)
                {
                    _entries.Add(normalized);
                    _force.Add(normalized);
                }
            }
        }
    }

    public IReadOnlyCollection<string> Entries => _entries;
    public IReadOnlyCollection<string> ForceEntries => _force;

    /// <summary>Loads the JSON overrides file; throws <see cref="GenericDeepenOverridesException"/> on malformed content.</summary>
    public static GenericDeepenOverrides Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new GenericDeepenOverridesException("the overrides root must be a JSON object.");
        }

        return new GenericDeepenOverrides(
            ReadArray(document.RootElement, "skip"),
            ReadArray(document.RootElement, "force"));
    }

    /// <summary>
    /// Whether a residual finding against <paramref name="member"/> (or its
    /// declaring <paramref name="type"/>) is explicitly overridden.
    /// <paramref name="matchedEntry"/> carries the matched fully-qualified name
    /// and <paramref name="force"/> says whether it is a force assertion.
    /// </summary>
    public bool IsSuppressed(
        ISymbol? member,
        ITypeSymbol? type,
        out string? matchedEntry,
        out bool force)
    {
        if (TryMatchMember(member, out matchedEntry)
            || TryMatchType(type, out matchedEntry))
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

        var candidates = new List<string>
        {
            NormalizeText(member.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
        };
        if (member.OriginalDefinition.ContainingType is { } containing)
        {
            // Roslyn's fully-qualified member form omits the containing type for
            // methods ("F<T>"); users name entries as "Type.Member" (optionally with
            // the generic parameter list), so build those forms too.
            var typePrefix = NormalizeText(
                containing.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            candidates.Add(typePrefix + "." + member.OriginalDefinition.Name);
            if (member.OriginalDefinition is IMethodSymbol method && method.TypeParameters.Length > 0)
            {
                var typeParameters = "<" + string.Join(", ", method.TypeParameters.Select(p => p.Name)) + ">";
                candidates.Add(typePrefix + "." + member.OriginalDefinition.Name + typeParameters);
            }
        }

        foreach (var candidate in candidates.Distinct())
        {
            if (_entries.Contains(candidate))
            {
                matchedEntry = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryMatchType(ITypeSymbol? type, out string? matchedEntry)
    {
        matchedEntry = null;
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        var exact = NormalizeText(named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        if (_entries.Contains(exact))
        {
            matchedEntry = exact;
            return true;
        }

        return false;
    }

    private static IEnumerable<string>? ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new GenericDeepenOverridesException($"property '{name}' must be a JSON array of strings.");
        }

        var entries = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new GenericDeepenOverridesException($"property '{name}' must contain strings only.");
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

/// <summary>Raised when a generic-deepen overrides file cannot be parsed.</summary>
public sealed class GenericDeepenOverridesException : Exception
{
    public GenericDeepenOverridesException(string message)
        : base(message)
    {
    }
}
