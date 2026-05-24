using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

internal sealed class RuntimeClassParameterRegistry
{
    private readonly Dictionary<string, IReadOnlyList<RuntimeClassTypeParameterRequirement>> _types =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RuntimeClassTypeParameterRequirement>> _methods =
        new(StringComparer.Ordinal);

    public bool TryGetTypeRequirements(
        INamedTypeSymbol typeSymbol,
        out IReadOnlyList<RuntimeClassTypeParameterRequirement> requirements)
        => _types.TryGetValue(GetTypeKey(typeSymbol), out requirements!);

    public void RegisterTypeRequirements(
        INamedTypeSymbol typeSymbol,
        IEnumerable<ITypeParameterSymbol> typeParameters)
    {
        var requirements = typeParameters
            .Select(RuntimeClassTypeParameterRequirement.From)
            .Where(requirement => requirement.Kind != RuntimeClassTypeParameterRequirementKind.Unknown)
            .Distinct()
            .ToArray();

        _types[GetTypeKey(typeSymbol)] = requirements;
    }

    public bool TryGetMethodRequirements(
        IMethodSymbol methodSymbol,
        out IReadOnlyList<RuntimeClassTypeParameterRequirement> requirements)
        => _methods.TryGetValue(GetMethodKey(methodSymbol), out requirements!);

    public void RegisterMethodRequirements(
        IMethodSymbol methodSymbol,
        IEnumerable<ITypeParameterSymbol> typeParameters)
    {
        var requirements = typeParameters
            .Select(RuntimeClassTypeParameterRequirement.From)
            .Where(requirement => requirement.Kind != RuntimeClassTypeParameterRequirementKind.Unknown)
            .Distinct()
            .ToArray();

        _methods[GetMethodKey(methodSymbol)] = requirements;
    }

    private static string GetTypeKey(INamedTypeSymbol typeSymbol)
        => typeSymbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string GetMethodKey(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        var containingType = GetTypeKey(originalMethod.ContainingType);
        var parameterTypes = string.Join(
            ",",
            originalMethod.Parameters.Select(parameter =>
                parameter.Type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

        return $"{containingType}.{originalMethod.MetadataName}`{originalMethod.TypeParameters.Length}({parameterTypes})";
    }
}

internal readonly record struct RuntimeClassTypeParameterRequirement(
    RuntimeClassTypeParameterRequirementKind Kind,
    int Ordinal,
    string Name)
{
    public static RuntimeClassTypeParameterRequirement From(ITypeParameterSymbol typeParameter)
    {
        if (typeParameter.DeclaringMethod != null)
        {
            return new RuntimeClassTypeParameterRequirement(
                RuntimeClassTypeParameterRequirementKind.Method,
                typeParameter.Ordinal,
                typeParameter.Name);
        }

        if (typeParameter.DeclaringType != null)
        {
            return new RuntimeClassTypeParameterRequirement(
                RuntimeClassTypeParameterRequirementKind.Type,
                typeParameter.Ordinal,
                typeParameter.Name);
        }

        return new RuntimeClassTypeParameterRequirement(
            RuntimeClassTypeParameterRequirementKind.Unknown,
            -1,
            typeParameter.Name);
    }
}

internal enum RuntimeClassTypeParameterRequirementKind
{
    Method,
    Type,
    Unknown,
}
