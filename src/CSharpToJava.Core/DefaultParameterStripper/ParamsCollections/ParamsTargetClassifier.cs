using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Classifies a params parameter's TYPE symbol into the C48 kind matrix
/// (design §2.1). Classification mirrors the C# 13 params-collections legal
/// set in compiler precedence order: array → span → [CollectionBuilder] →
/// five interfaces → collection-initializer pattern → unsupported.
///
/// The parameter symbol itself is never inspected for params attributes:
/// source symbols carry neither ParamArrayAttribute nor
/// ParamCollectionAttribute (M0 probe Q2). [CollectionBuilder] IS read from
/// the type symbol's attributes (probe Q7). Baseline-illegal shapes (params
/// StringBuilder, multi-dimensional arrays, non-generic IEnumerable) cannot
/// appear in compilable input (probe Q9) — they land in
/// <see cref="ParamsParamKind.Unsupported"/> with CS2J0601 as a defensive net.
/// </summary>
public static class ParamsTargetClassifier
{
    public const string SpanMetadataName = "System.Span`1";
    public const string ReadOnlySpanMetadataName = "System.ReadOnlySpan`1";
    public const string CanonicalListMetadataName = "System.Collections.Generic.List`1";
    public const string GenericEnumerableMetadataName = "System.Collections.Generic.IEnumerable`1";
    public const string CollectionBuilderAttributeMetadataName =
        "System.Runtime.CompilerServices.CollectionBuilderAttribute";

    /// <summary>The five interfaces legal for params collections (design §1.1).</summary>
    private static readonly string[] InterfaceMetadataNames =
    {
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
    };

    public static ParamsTargetClassification Classify(ITypeSymbol parameterType, Compilation compilation)
    {
        if (parameterType.TypeKind == TypeKind.Error)
        {
            return Unsupported(parameterType, ParamsDiagnostics.UnsupportedType);
        }

        if (parameterType is IArrayTypeSymbol array)
        {
            // Multi-dimensional arrays are baseline-illegal for params (CS0225,
            // probe Q9) and only reachable on non-compilable input.
            return array.Rank == 1
                ? new ParamsTargetClassification(
                    ParamsParamKind.Array, parameterType, array.ElementType,
                    CanonicalConcreteType: null, BuilderInfo: null,
                    ParamsEmptyStateShape.ArrayEmpty)
                : Unsupported(parameterType, ParamsDiagnostics.UnsupportedType);
        }

        if (parameterType is not INamedTypeSymbol named)
        {
            return Unsupported(parameterType, ParamsDiagnostics.UnsupportedType);
        }

        if (IsSpan(named, compilation, out var spanElement))
        {
            // Ref-struct elements are rejected at declaration level by the C#
            // compiler (CS9244, probe Q6) — this is a defensive path only.
            return spanElement!.IsRefLikeType
                ? new ParamsTargetClassification(
                    ParamsParamKind.Unsupported, named, spanElement,
                    CanonicalConcreteType: null, BuilderInfo: null,
                    ParamsEmptyStateShape.NewInstance,
                    SkipReason: ParamsDiagnostics.RefStructElement)
                : new ParamsTargetClassification(
                    ParamsParamKind.Span, named, spanElement,
                    CanonicalConcreteType: null, BuilderInfo: null,
                    ParamsEmptyStateShape.ArrayEmpty);
        }

        var builderInfo = ReadCollectionBuilder(named, compilation);
        if (builderInfo != null)
        {
            return new ParamsTargetClassification(
                ParamsParamKind.Builder, named,
                TryGetEnumerableElementType(named, compilation),
                CanonicalConcreteType: null, BuilderInfo: builderInfo,
                ParamsEmptyStateShape.NewInstance);
        }

        if (IsLegalParamsInterface(named, compilation, out var interfaceElement))
        {
            // K5: canonical concrete class is List<T> — implicitly convertible
            // to all five interfaces (C36 §2.2 mirror table).
            var listDefinition = compilation.GetTypeByMetadataName(CanonicalListMetadataName);
            var canonical = listDefinition?.Construct(interfaceElement!);
            return new ParamsTargetClassification(
                ParamsParamKind.Interface, named, interfaceElement,
                CanonicalConcreteType: canonical, BuilderInfo: null,
                ParamsEmptyStateShape.ArrayEmpty);
        }

        return ClassifyInitializable(named, compilation);
    }

    // ------------------------------------------------------------------
    // Span (K2)
    // ------------------------------------------------------------------

    private static bool IsSpan(
        INamedTypeSymbol candidate,
        Compilation compilation,
        out ITypeSymbol? elementType)
    {
        elementType = null;
        var definition = candidate.OriginalDefinition;
        foreach (var metadataName in new[] { SpanMetadataName, ReadOnlySpanMetadataName })
        {
            var spanDefinition = compilation.GetTypeByMetadataName(metadataName);
            if (spanDefinition == null
                || !SymbolEqualityComparer.Default.Equals(definition, spanDefinition))
            {
                continue;
            }

            elementType = candidate.TypeArguments[0];
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // CollectionBuilder (K3)
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads [CollectionBuilder(typeof(B), "Create")] from the type symbol's
    /// attributes (probe Q7: constructor arguments are Type + string).
    /// </summary>
    private static CollectionBuilderInfo? ReadCollectionBuilder(
        INamedTypeSymbol type,
        Compilation compilation)
    {
        var attributeDefinition = compilation.GetTypeByMetadataName(CollectionBuilderAttributeMetadataName);
        if (attributeDefinition == null)
        {
            return null;
        }

        foreach (var attribute in type.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass == null
                || !SymbolEqualityComparer.Default.Equals(attributeClass.OriginalDefinition, attributeDefinition))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length < 2)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is not INamedTypeSymbol builderType
                || attribute.ConstructorArguments[1].Value is not string factoryName)
            {
                continue;
            }

            return new CollectionBuilderInfo(builderType, factoryName);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Five interfaces (K5)
    // ------------------------------------------------------------------

    private static bool IsLegalParamsInterface(
        INamedTypeSymbol candidate,
        Compilation compilation,
        out ITypeSymbol? elementType)
    {
        elementType = null;
        var definition = candidate.OriginalDefinition;
        foreach (var metadataName in InterfaceMetadataNames)
        {
            var interfaceDefinition = compilation.GetTypeByMetadataName(metadataName);
            if (interfaceDefinition == null
                || !SymbolEqualityComparer.Default.Equals(definition, interfaceDefinition))
            {
                continue;
            }

            elementType = candidate.TypeArguments[0];
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Collection-initializer pattern (K4)
    // ------------------------------------------------------------------

    private static ParamsTargetClassification ClassifyInitializable(
        INamedTypeSymbol type,
        Compilation compilation)
    {
        var elementType = TryGetEnumerableElementType(type, compilation);
        if (elementType == null)
        {
            return Unsupported(type, ParamsDiagnostics.UnsupportedType);
        }

        // Conservative visibility check; the M2 emitter switches to IOperation
        // Add binding (same discipline as C24: never guess add/put).
        if (!HasAccessibleParameterlessConstructor(type) || !HasAccessibleInstanceAdd(type))
        {
            return new ParamsTargetClassification(
                ParamsParamKind.Unsupported, type, elementType,
                CanonicalConcreteType: null, BuilderInfo: null,
                ParamsEmptyStateShape.NewInstance,
                SkipReason: ParamsDiagnostics.InitializerPatternBroken);
        }

        return new ParamsTargetClassification(
            ParamsParamKind.Initializable, type, elementType,
            CanonicalConcreteType: type, BuilderInfo: null,
            ParamsEmptyStateShape.NewInstance);
    }

    /// <summary>
    /// Finds the element type T of the implemented (generic) IEnumerable&lt;T&gt;,
    /// or null when the type does not implement it (non-generic
    /// System.Collections.IEnumerable is not part of the legal params set).
    /// </summary>
    private static ITypeSymbol? TryGetEnumerableElementType(
        INamedTypeSymbol type,
        Compilation compilation)
    {
        var enumerableDefinition = compilation.GetTypeByMetadataName(GenericEnumerableMetadataName);
        if (enumerableDefinition == null)
        {
            return null;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, enumerableDefinition))
            {
                return implemented.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>Conservative accessibility: same-compilation visibility is enough (public/internal/protected-internal).</summary>
    private static bool IsAccessible(Accessibility accessibility)
        => accessibility is Accessibility.Public
            or Accessibility.Internal
            or Accessibility.ProtectedOrInternal;

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.IsValueType)
        {
            // Structs have an implicit always-accessible default constructor
            // unless one is declared explicitly.
            var declared = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
            return declared == null || IsAccessible(declared.DeclaredAccessibility);
        }

        return type.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && IsAccessible(c.DeclaredAccessibility));
    }

    private static bool HasAccessibleInstanceAdd(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers("Add"))
        {
            if (member is not IMethodSymbol method
                || method.IsStatic
                || method.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }

            if (IsAccessible(method.DeclaredAccessibility) && method.Parameters.Length >= 1)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ParamsTargetClassification Unsupported(ITypeSymbol parameterType, string skipReason)
        => new(
            ParamsParamKind.Unsupported, parameterType, ElementType: null,
            CanonicalConcreteType: null, BuilderInfo: null,
            ParamsEmptyStateShape.NewInstance, SkipReason: skipReason);
}
