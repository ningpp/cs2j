using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// Classification of a generic type parameter based on how it is bound
/// across all subclass instantiations in the compilation.
/// </summary>
public enum TypeParameterBindingKind
{
    /// <summary>All subclass bindings are to the same struct type.</summary>
    AlwaysSameStruct,
    /// <summary>All subclass bindings are to reference types.</summary>
    AlwaysReference,
    /// <summary>Mixed bindings or unable to determine — fall back to current behavior.</summary>
    Unknown,
}

/// <summary>
/// Result of analyzing a type parameter's subclass bindings.
/// </summary>
public sealed class TypeParameterBindingResult
{
    public TypeParameterBindingKind Kind { get; }
    /// <summary>The concrete struct type symbol when Kind is AlwaysSameStruct; null otherwise.</summary>
    public INamedTypeSymbol? ConcreteStructType { get; }

    public TypeParameterBindingResult(TypeParameterBindingKind kind, INamedTypeSymbol? concreteStructType = null)
    {
        Kind = kind;
        ConcreteStructType = concreteStructType;
    }
}

/// <summary>
/// Pre-analysis pass that classifies each type parameter of each generic class
/// by walking subclass instantiations in the full compilation.
/// Results are cached for O(1) lookups during conversion.
/// </summary>
public sealed class TypeParameterBindingAnalyzer
{
    private readonly Dictionary<string, TypeParameterBindingResult> _results = new();

    internal TypeParameterBindingAnalyzer() { }

    public TypeParameterBindingResult GetBinding(ITypeParameterSymbol typeParam)
    {
        if (typeParam.ContainingType is not INamedTypeSymbol containingType)
            return new TypeParameterBindingResult(TypeParameterBindingKind.Unknown);

        var key = $"{GetFullMetadataName(containingType.OriginalDefinition)}|{typeParam.Name}";
        return _results.TryGetValue(key, out var result)
            ? result
            : new TypeParameterBindingResult(TypeParameterBindingKind.Unknown);
    }

    public static TypeParameterBindingAnalyzer Analyze(Compilation compilation)
    {
        var analyzer = new TypeParameterBindingAnalyzer();

        // Collect all subclass→base bindings.
        // Key: "fullMetadataName|typeParamIndex" → list of (boundType, isStruct)
        var rawBindings = new Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>>();

        WalkNamespace(compilation.GlobalNamespace, rawBindings);

        // Classify each type parameter.
        foreach (var kvp in rawBindings)
        {
            var key = kvp.Key;
            var bindings = kvp.Value;

            // Parse key: "fullMetadataName|typeParamIndex"
            var sepIdx = key.LastIndexOf('|');
            var fullName = key.Substring(0, sepIdx);
            var typeParamIndex = int.Parse(key.Substring(sepIdx + 1));

            if (bindings.Count == 0)
                continue;

            bool allStruct = bindings.All(b => b.IsStruct);
            bool allReference = bindings.All(b => !b.IsStruct);

            if (allReference)
            {
                // Find the original type symbol to get the type param name.
                // We don't have the symbol readily available from the string key,
                // so we store results later when we have type symbols.
                continue;
            }

            if (allStruct && bindings.All(b => b.BoundType != null))
            {
                // Check if all bindings are to the same struct type.
                var firstStruct = bindings[0].BoundType;
                bool allSameStruct = bindings.All(b =>
                    SymbolEqualityComparer.Default.Equals(b.BoundType, firstStruct));

                if (allSameStruct && firstStruct != null)
                {
                    // Store with temporary key — we'll need the type param name from the symbol.
                    // For now, store an entry that can be looked up by the generic type definition.
                }
            }
        }

        // Second pass: build results using type symbols.
        BuildResults(compilation.GlobalNamespace, rawBindings, analyzer._results);

        return analyzer;
    }

    private static void BuildResults(
        INamespaceSymbol globalNamespace,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings,
        Dictionary<string, TypeParameterBindingResult> results)
    {
        foreach (var member in globalNamespace.GetMembers())
        {
            if (member is INamedTypeSymbol namedType && namedType.TypeParameters.Length > 0)
                ClassifyTypeParameters(namedType, rawBindings, results);
            if (member is INamespaceOrTypeSymbol nsOrType)
                BuildResultsInNamespace(nsOrType, rawBindings, results);
        }
    }

    private static void BuildResultsInNamespace(
        INamespaceOrTypeSymbol namespaceOrType,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings,
        Dictionary<string, TypeParameterBindingResult> results)
    {
        foreach (var member in namespaceOrType.GetMembers())
        {
            if (member is INamedTypeSymbol namedType && namedType.TypeParameters.Length > 0)
                ClassifyTypeParameters(namedType, rawBindings, results);
            if (member is INamespaceOrTypeSymbol nsOrType)
                BuildResultsInNamespace(nsOrType, rawBindings, results);
        }
    }

    private static void ClassifyTypeParameters(
        INamedTypeSymbol genericType,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings,
        Dictionary<string, TypeParameterBindingResult> results)
    {
        for (int i = 0; i < genericType.TypeParameters.Length; i++)
        {
            var typeParam = genericType.TypeParameters[i];
            var resultKey = $"{GetFullMetadataName(genericType)}|{typeParam.Name}";
            var rawKey = $"{GetFullMetadataName(genericType)}|{i}";

            if (!rawBindings.TryGetValue(rawKey, out var bindings) || bindings.Count == 0)
            {
                results[resultKey] = new TypeParameterBindingResult(TypeParameterBindingKind.Unknown);
                continue;
            }

            bool allStruct = bindings.All(b => b.IsStruct);
            bool allReference = bindings.All(b => !b.IsStruct);

            if (allReference)
            {
                results[resultKey] = new TypeParameterBindingResult(TypeParameterBindingKind.AlwaysReference);
            }
            else if (allStruct && bindings.All(b => b.BoundType != null))
            {
                var firstStruct = bindings[0].BoundType;
                bool allSameStruct = bindings.All(b =>
                    SymbolEqualityComparer.Default.Equals(b.BoundType, firstStruct));

                if (allSameStruct && firstStruct != null)
                {
                    results[resultKey] = new TypeParameterBindingResult(
                        TypeParameterBindingKind.AlwaysSameStruct, firstStruct);
                }
                else
                {
                    results[resultKey] = new TypeParameterBindingResult(TypeParameterBindingKind.Unknown);
                }
            }
            else
            {
                results[resultKey] = new TypeParameterBindingResult(TypeParameterBindingKind.Unknown);
            }
        }
    }

    private static void WalkNamespace(
        INamespaceOrTypeSymbol namespaceOrType,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings)
    {
        foreach (var member in namespaceOrType.GetMembers())
        {
            if (member is INamespaceOrTypeSymbol nsOrType)
                WalkNamespace(nsOrType, rawBindings);

            if (member is INamedTypeSymbol namedType && namedType.TypeKind != TypeKind.Error)
                ProcessType(namedType, rawBindings);
        }
    }

    private static void ProcessType(
        INamedTypeSymbol type,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings)
    {
        // Process base type
        if (type.BaseType is INamedTypeSymbol baseType && baseType.IsGenericType)
            RecordBindings(baseType, rawBindings);

        // Also check interfaces
        foreach (var iface in type.Interfaces)
        {
            if (iface.IsGenericType)
                RecordBindings(iface, rawBindings);
        }
    }

    private static void RecordBindings(
        INamedTypeSymbol constructedGenericType,
        Dictionary<string, List<(INamedTypeSymbol? BoundType, bool IsStruct)>> rawBindings)
    {
        var originalDef = constructedGenericType.OriginalDefinition;
        if (originalDef.TypeParameters.Length == 0)
            return;

        var typeArgs = constructedGenericType.TypeArguments;
        for (int i = 0; i < typeArgs.Length; i++)
        {
            var typeArg = typeArgs[i];
            if (typeArg is INamedTypeSymbol namedArg && namedArg.TypeKind != TypeKind.Error)
            {
                bool isStruct = namedArg.TypeKind == TypeKind.Struct
                    && namedArg.SpecialType == SpecialType.None
                    && namedArg.OriginalDefinition.SpecialType == SpecialType.None
                    && namedArg.OriginalDefinition.ToDisplayString() != "System.Nullable<T>";

                var key = $"{GetFullMetadataName(originalDef)}|{i}";
                if (!rawBindings.TryGetValue(key, out var list))
                {
                    list = new List<(INamedTypeSymbol?, bool)>();
                    rawBindings[key] = list;
                }
                list.Add((namedArg, isStruct));
            }
        }
    }

    internal static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        var name = type.MetadataName; // Includes `arity suffix
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
