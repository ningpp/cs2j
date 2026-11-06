using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata.ConsumerSurfaces;

/// <summary>Default symbol matcher over a list of configured consumer-surface entries.</summary>
public sealed class ClosedTypeConsumerProfile : IClosedTypeConsumerProfile
{
    public ClosedTypeConsumerProfile(string name, IReadOnlyList<ClosedTypeConsumerSurfaceEntry> entries)
    {
        Name = name;
        Entries = entries;
    }

    public string Name { get; }

    public IReadOnlyList<ClosedTypeConsumerSurfaceEntry> Entries { get; }

    public bool IsEntry(IMethodSymbol method)
    {
        var containing = method.ContainingType;
        if (containing is null)
        {
            return false;
        }

        var displayName = containing.ToDisplayString();
        foreach (var entry in Entries)
        {
            if (!string.Equals(entry.Type, displayName, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Methods.Count > 0
                && !entry.Methods.Contains(method.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (!MatchesAssembly(entry.Assembly, containing))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Metadata symbols must belong to the configured assembly; source-declared
    /// shadow types keep the historical name-only match (test fixtures and
    /// source-generated shadows bind against the consumer project's assembly).
    /// </summary>
    private static bool MatchesAssembly(string entryAssembly, INamedTypeSymbol containing)
    {
        if (string.IsNullOrEmpty(entryAssembly))
        {
            return true;
        }

        var assembly = containing.ContainingAssembly;
        if (assembly is null || assembly.Locations.Any(location => location.IsInSource))
        {
            return true;
        }

        return string.Equals(assembly.Identity.Name, entryAssembly, StringComparison.Ordinal);
    }
}
