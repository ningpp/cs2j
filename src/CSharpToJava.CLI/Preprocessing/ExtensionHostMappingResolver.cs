using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Builds the M3 external-host mapping grader for the extension-methods pass
/// (design §4.2-3/§5.1): the Core pass only consumes the
/// <see cref="ExternalHostMappingKind"/> label; this resolver provides the
/// TypeMappings-backed decision on the CLI side.
///
/// A host is <see cref="ExternalHostMappingKind.Mapped"/> when the host type
/// itself has a type mapping or at least one of its extension methods has a
/// method mapping — the same lookup surface the converter's CS2J_EXT001 check
/// uses (<see cref="TypeMappingRegistry.MapMethod(string,string)"/>).
///
/// If the mapping configuration cannot be loaded, the conservative grader
/// returns <see cref="ExternalHostMappingKind.Unknown"/> for every host: the
/// run stays loud (CS2J1203 per unmapped site) instead of silently pretending
/// every metadata host is covered.
/// </summary>
internal static class ExtensionHostMappingResolver
{
    public static Func<INamedTypeSymbol, ExternalHostMappingKind> TryCreate(string? configPath)
    {
        TypeMappingRegistry registry;
        try
        {
            registry = new TypeMappingRegistry(configPath);
        }
        catch (TypeMappingConfigurationException)
        {
            return static _ => ExternalHostMappingKind.Unknown;
        }

        return host => Classify(host, registry);
    }

    private static ExternalHostMappingKind Classify(
        INamedTypeSymbol host,
        TypeMappingRegistry registry)
    {
        foreach (var typeName in CandidateTypeNames(host))
        {
            if (registry.HasTypeMapping(typeName))
            {
                return ExternalHostMappingKind.Mapped;
            }

            // Mirror the converter's ProjectExtensionMethodCheckPass lookup:
            // MapMethod(type, methodName) on the extension methods the host
            // declares (reduced/ordinary static forms both report IsExtensionMethod).
            foreach (var member in host.GetMembers())
            {
                if (member is IMethodSymbol { IsExtensionMethod: true } method
                    && registry.MapMethod(typeName, method.Name) != null)
                {
                    return ExternalHostMappingKind.Mapped;
                }
            }
        }

        return ExternalHostMappingKind.Unknown;
    }

    private static IEnumerable<string> CandidateTypeNames(INamedTypeSymbol host)
    {
        yield return host.ToDisplayString();

        if (!SymbolEqualityComparer.Default.Equals(host, host.OriginalDefinition))
        {
            yield return host.OriginalDefinition.ToDisplayString();
        }

        var constructedFrom = host.ConstructedFrom;
        if (!SymbolEqualityComparer.Default.Equals(constructedFrom, host))
        {
            yield return constructedFrom.ToDisplayString();
        }
    }
}
