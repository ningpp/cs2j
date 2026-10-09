using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Shared holder-reachability math for CM-LINK: the M4b consumer linker and the
/// M4c residual scanner must agree exactly on which holders serve a serializer
/// entry, so a site the linker could guard is never later reported as
/// unguarded (or vice versa). A holder is reachable from a consumer project when
/// it is that project's own holder, or the closed construction is declared in
/// source (naming the type already requires the declaring project reference);
/// framework closures owned by an unrelated project stay unreachable.
/// </summary>
internal static class ClosedMetadataGuardResolver
{
    public static Dictionary<string, string> BuildHolderByRecordKey(
        ClosedMetadataAnalysis analysis,
        Func<string, string?> holderNameByPath,
        out HashSet<string> holderNames)
    {
        var byRecordKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in analysis.ClosedTypes)
        {
            var holder = holderNameByPath(record.DeclaringFilePath);
            if (holder is null)
            {
                continue;
            }

            names.Add(holder);
            byRecordKey.TryAdd(record.Key.SortText, holder);
        }

        holderNames = names;
        return byRecordKey;
    }

    public static SortedSet<string> ResolveHolderNames(
        ImmutableArray<ITypeSymbol> typeArguments,
        Dictionary<string, string> holderByRecordKey,
        HashSet<string> holderNames,
        string? selfHolderName,
        Func<string, string?> holderNameByPath,
        bool requireReachable = true)
    {
        var holders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var typeArgument in typeArguments)
        {
            foreach (var type in EnumerateTypeClosure(typeArgument))
            {
                if (type is not INamedTypeSymbol named)
                {
                    continue;
                }

                if (named.IsGenericType && !named.IsDefinition)
                {
                    if (ClosedTypeKey.Create(named) is { } key
                        && holderByRecordKey.TryGetValue(key.SortText, out var holder)
                        && (!requireReachable || IsReachableHolder(holder, selfHolderName, named)))
                    {
                        holders.Add(holder);
                    }

                    continue;
                }

                if (!named.IsGenericType)
                {
                    // Non-generic user DTO: its member closures are registered in its
                    // declaring project; framework types have no source location.
                    var sourcePath = named.Locations
                        .FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
                    if (sourcePath is not null
                        && holderNameByPath(sourcePath) is { } ownerHolder
                        && holderNames.Contains(ownerHolder))
                    {
                        holders.Add(ownerHolder);
                    }
                }
            }
        }

        return holders;
    }

    /// <summary>
    /// A framework-construction record may be owned by an unrelated project (its
    /// first ENUM-1 site); cross-references are only guaranteed for user-declared
    /// types or for the consumer's own holder.
    /// </summary>
    public static bool IsReachableHolder(string holder, string? selfHolderName, INamedTypeSymbol constructedType)
    {
        if (holder == selfHolderName)
        {
            return true;
        }

        return constructedType.Locations.Any(location => location.IsInSource);
    }

    public static IEnumerable<ITypeSymbol> EnumerateTypeClosure(ITypeSymbol type)
    {
        yield return type;
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var nested in EnumerateTypeClosure(array.ElementType))
                {
                    yield return nested;
                }

                break;
            case INamedTypeSymbol { IsDefinition: false } named:
                foreach (var argument in named.TypeArguments)
                {
                    foreach (var nested in EnumerateTypeClosure(argument))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
