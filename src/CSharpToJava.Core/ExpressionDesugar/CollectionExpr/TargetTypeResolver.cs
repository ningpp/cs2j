using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Result of target classification for one <see cref="CollectionExpressionSyntax"/> site.
/// </summary>
internal sealed record TargetResolution(
    TargetKind Kind,
    ITypeSymbol? ConvertedType,
    bool IsMissingOrError,
    ITypeSymbol? TargetElementType = null,
    IMethodSymbol? AddMethod = null,
    INamedTypeSymbol? BuilderType = null,
    string? BuilderFactoryMethod = null,
    int ArrayRank = 0,
    INamedTypeSymbol? ConstructedType = null,
    IMethodSymbol? BuilderFactory = null,
    string? BuilderResolveError = null,
    string? BaselineNote = null,
    IMethodSymbol? ConstructibleConstructor = null);

/// <summary>
/// Classifies a collection-expression site by
/// <c>SemanticModel.GetTypeInfo(node).ConvertedType</c> — the only allowed
/// source of truth (design hard invariant #1; source text is never used).
///
/// M1 dispatch:
/// <list type="bullet">
/// <item>T1 rank-1 arrays (multidimensional arrays are flagged for 0581);</item>
/// <item>T2 Span/ReadOnlySpan (symbol full name; M1 emits the array form);</item>
/// <item>T5 [CollectionBuilder] metadata (M1 skips 0580, M2 emits factory);</item>
/// <item>interface targets (M1 skips 0581; M2 maps to specification concrete types);</item>
/// <item>T3 concrete class/struct: accessible parameterless instance ctor plus
/// an accessible single-parameter Add whose parameter implicitly accepts
/// every element (symbol-verified, never text-guessed).</item>
/// </list>
/// </summary>
internal static class TargetTypeResolver
{
    private const string ListOfTMetadataName = "System.Collections.Generic.List`1";

    /// <summary>
    /// The five generic interfaces the C# compiler itself constructs via a
    /// collection expression (locked by the M0/M2 Roslyn 5.0 probe, plan
    /// §6.4): every other interface (IDictionary&lt;K,V&gt;,
    /// IReadOnlyDictionary&lt;K,V&gt;, ISet&lt;T&gt;, the non-generic
    /// System.Collections interfaces, ...) is baseline CS9174
    /// "not constructible" and unreachable from compilable input.
    /// </summary>
    private static readonly string[] ConstructibleInterfaceMetadataNames =
    {
        "System.Collections.Generic.IEnumerable<T>",
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.IReadOnlyList<T>",
    };

    public static TargetResolution Resolve(
        SemanticModel model,
        CollectionExpressionSyntax node,
        IReadOnlyList<ITypeSymbol?> elementFedTypes)
    {
        var converted = GetTargetType(model, node);

        if (IsMissingOrError(converted))
        {
            return new TargetResolution(
                TargetKind.Unknown, converted, IsMissingOrError: true);
        }

        // T1: array. Rank > 1 (multidimensional) has no M1 form (0581); jagged
        // arrays are rank 1 of an array element type and stay T1.
        if (converted is IArrayTypeSymbol array)
        {
            return new TargetResolution(
                array.Rank > 1 ? TargetKind.Unknown : TargetKind.Array,
                converted, false,
                TargetElementType: array.Rank == 1 ? array.ElementType : null,
                ArrayRank: array.Rank);
        }

        // T2: Span<T>/ReadOnlySpan<T> (symbol full name, never source text).
        if (converted is INamedTypeSymbol spanNamed && IsSpanLike(spanNamed))
        {
            var elementType = spanNamed.TypeArguments is { Length: 1 } spanArgs
                ? spanArgs[0]
                : null;
            return new TargetResolution(
                TargetKind.Span, converted, false, TargetElementType: elementType);
        }

        if (converted is INamedTypeSymbol named)
        {
            // T5: [CollectionBuilder(typeof(B), "Create")]. M2 resolves the
            // unique static factory from attribute metadata + symbols (the
            // bound Create call never enters the IOperation tree, plan §6.3).
            if (CollectionBuilderAnalyzer.TryGetBuilder(
                    named, out var builderType, out var factoryMethod))
            {
                var builderResolved = CollectionBuilderAnalyzer.TryResolveFactory(
                    (CSharpCompilation)model.Compilation,
                    named, builderType!, factoryMethod!,
                    elementFedTypes,
                    out var factory, out var resolveError);

                return new TargetResolution(
                    TargetKind.CollectionBuilder, converted, false,
                    TargetElementType: factory?.ElementType,
                    BuilderType: builderType,
                    BuilderFactoryMethod: factoryMethod,
                    BuilderFactory: factory?.Factory,
                    BuilderResolveError: builderResolved ? null : resolveError);
            }

            // T4: interface targets. Only the five constructible generic
            // interfaces map to List<T>; all others are baseline CS9174 and
            // unreachable from legal input (defensive 0581 skip).
            if (named.TypeKind == TypeKind.Interface)
            {
                var concrete = TrySelectInterfaceConcreteType(model.Compilation, named);
                var concreteValue = concrete.GetValueOrDefault();
                return concrete != null
                    ? new TargetResolution(
                        TargetKind.Interface, converted, false,
                        TargetElementType: concreteValue.ElementType,
                        ConstructedType: concreteValue.ListType)
                    : new TargetResolution(
                        TargetKind.Interface, converted, false,
                        BaselineNote: InterfaceBaselineNote);
            }

            var currentAssembly = model.Compilation.Assembly;

            // T3: concrete addable class/struct only. The classic collection
            // initializer emitted by the zero-temp form (and the native
            // collection-expression binding itself) requires
            // System.Collections.IEnumerable — a type with Add but without the
            // marker would make the output fail CS1922.
            if (named.TypeKind is TypeKind.Class or TypeKind.Struct
                && !named.IsStatic && !named.IsAbstract
                && ImplementsNonGenericEnumerable(named))
            {
                if (HasAccessibleParameterlessConstructor(named, currentAssembly)
                    && TryFindPatternAdd(
                        (CSharpCompilation)model.Compilation,
                        named, currentAssembly, elementFedTypes,
                        out var addMethod, out var elementType))
                {
                    return new TargetResolution(
                        TargetKind.MutableConcrete, converted, false,
                        TargetElementType: elementType, AddMethod: addMethod);
                }
            }

            // T6: concrete class/struct T3 did not accept, but constructible
            // through the C# 12 collection-expression constructor shapes
            // (rt10 JsonArray: `JsonArray(JsonNodeOptions? options = null)`,
            // `JsonArray(params JsonNode?[] items)`,
            // `JsonArray(params ReadOnlySpan<JsonNode?> items)`). The target
            // must still implement System.Collections.IEnumerable: the
            // baseline compiler rejects every other class/struct with CS9174,
            // so this branch never invents construction semantics for types
            // the language itself cannot target.
            if (named.TypeKind is TypeKind.Class or TypeKind.Struct
                && !named.IsStatic && !named.IsAbstract
                && ImplementsNonGenericEnumerable(named)
                && IsAccessible(named, currentAssembly))
            {
                if (node.Elements.Count == 0)
                {
                    if (TryFindNoRequiredArgumentConstructor(
                            named, currentAssembly, out var emptyConstructor))
                    {
                        return new TargetResolution(
                            TargetKind.ConstructibleClass, converted, false,
                            ConstructedType: named,
                            ConstructibleConstructor: emptyConstructor);
                    }
                }
                else if (TryFindConstructibleElementsConstructor(
                             named, currentAssembly,
                             out var elementsConstructor, out var constructibleElement))
                {
                    return new TargetResolution(
                        TargetKind.ConstructibleClass, converted, false,
                        TargetElementType: constructibleElement,
                        ConstructedType: named,
                        ConstructibleConstructor: elementsConstructor);
                }
            }
        }

        return new TargetResolution(TargetKind.Unknown, converted, false);
    }

    /// <summary>
    /// Target type of <paramref name="node"/>: its own
    /// <c>ConvertedType</c> when the semantic model bound one, otherwise the
    /// converted type of the nearest enclosing construct that propagates its
    /// target type to this position (switch-expression arm, <c>?:</c> branch,
    /// <c>??</c> right operand, parenthesized expression). Only these
    /// target-propagating positions are followed; any other parent stops the
    /// walk so the site keeps its missing-target diagnostic instead of
    /// guessing a type from an unrelated enclosing expression. Ambiguous
    /// simple names (CS0104 from the merged analysis compilation) are rebound
    /// to the single source-declared candidate, exactly like the initializer
    /// rules do.
    /// </summary>
    private static ITypeSymbol? GetTargetType(
        SemanticModel model, CollectionExpressionSyntax node)
    {
        var converted = PreferSourceDeclaredCandidate(
            model.GetTypeInfo(node).ConvertedType);
        if (!IsMissingOrError(converted))
            return converted;

        SyntaxNode child = node;
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (!PropagatesTargetType(current, child))
                break;

            var propagated = PreferSourceDeclaredCandidate(
                model.GetTypeInfo(current).ConvertedType);
            if (!IsMissingOrError(propagated))
                return propagated;

            child = current;
        }

        return converted;
    }

    private static ITypeSymbol? PreferSourceDeclaredCandidate(ITypeSymbol? type)
        => ExpressionDesugarSyntaxFacts.PreferSourceDeclaredCandidate(type);

    /// <summary>
    /// True when <paramref name="parent"/> converts its direct child
    /// <paramref name="child"/> to the parent's own converted type.
    /// </summary>
    private static bool PropagatesTargetType(SyntaxNode parent, SyntaxNode child)
        => parent switch
        {
            SwitchExpressionArmSyntax arm
                when arm.Expression == child => true,
            SwitchExpressionSyntax switchExpression
                when child is SwitchExpressionArmSyntax arm
                     && switchExpression.Arms.Contains(arm) => true,
            ConditionalExpressionSyntax conditional
                when conditional.WhenTrue == child
                     || conditional.WhenFalse == child => true,
            BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.CoalesceExpression)
                     && binary.Right == child => true,
            ParenthesizedExpressionSyntax parenthesized
                when parenthesized.Expression == child => true,
            _ => false,
        };

    /// <summary>
    /// CS9174 note appended to the defensive 0581 message for interface
    /// targets the C# compiler itself cannot construct (unreachable in legal
    /// input; no Dictionary/List&lt;object?&gt; emission is invented).
    /// </summary>
    public const string InterfaceBaselineNote =
        "baseline C# reports CS9174 (collection expressions cannot construct " +
        "this interface); the site is unreachable from compilable input";

    /// <summary>
    /// CE-4 (design §2.2): maps one of the five constructible generic
    /// collection interfaces to <c>List&lt;T&gt;</c>, where T is the
    /// interface's single type argument (the element type). Null for every
    /// other interface.
    /// </summary>
    public static (INamedTypeSymbol ListType, ITypeSymbol ElementType)?
        TrySelectInterfaceConcreteType(Compilation compilation, INamedTypeSymbol iface)
    {
        if (!iface.IsGenericType
            || iface.TypeArguments is not { Length: 1 } typeArguments
            || !ConstructibleInterfaceMetadataNames.Contains(
                iface.OriginalDefinition.ToDisplayString()))
        {
            return null;
        }

        var elementType = typeArguments[0];
        if (elementType.Kind == SymbolKind.ErrorType)
            return null;

        var listDefinition = compilation.GetTypeByMetadataName(ListOfTMetadataName);
        if (listDefinition == null)
            return null;

        return (listDefinition.Construct(elementType), elementType);
    }

    /// <summary>
    /// Rank of an array visible at <paramref name="node"/> from either the
    /// converted or the natural type (a multidimensional-array target site can
    /// carry a compiler error while still exposing the rank-2 array symbol).
    /// </summary>
    public static int TryGetArrayRank(SemanticModel model, CollectionExpressionSyntax node)
    {
        var info = model.GetTypeInfo(node);
        var rank = (info.ConvertedType as IArrayTypeSymbol)?.Rank;
        rank ??= (info.Type as IArrayTypeSymbol)?.Rank;
        return rank ?? 0;
    }

    /// <summary>True for null, error-type symbols or the unresolved type marker.</summary>
    public static bool IsMissingOrError(ITypeSymbol? type)
        => type == null
           || type.TypeKind == TypeKind.Error
           || type is IErrorTypeSymbol;

    /// <summary>
    /// Symbol full-name check for System.Span&lt;T&gt; and
    /// System.ReadOnlySpan&lt;T&gt; (open original definition).
    /// </summary>
    public static bool IsSpanLike(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || !named.IsGenericType)
            return false;

        return named.OriginalDefinition.ToDisplayString() is
            "System.Span<T>" or "System.ReadOnlySpan<T>";
    }

    /// <summary>
    /// Accessibility for the pattern matching M1 performs: public members are
    /// visible everywhere; internal/private/protected members are visible when
    /// the pattern type lives in the compiling assembly (same metadata scope
    /// as the collection-expression site).
    /// </summary>
    public static bool IsAccessible(ISymbol symbol, IAssemblySymbol currentAssembly)
        => symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.NotApplicable => true,
            _ => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingAssembly, currentAssembly),
        };

    /// <summary>
    /// T6 constructor accessibility. Unlike <see cref="IsAccessible"/> (which
    /// treats every same-assembly symbol as reachable for the pattern check),
    /// private/protected constructors can never be invoked from an arbitrary
    /// collection-expression site, so they are rejected here rather than
    /// emitting output that fails CS0122.
    /// </summary>
    private static bool IsConstructibleConstructorAccessible(
        IMethodSymbol constructor, IAssemblySymbol currentAssembly)
        => constructor.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.NotApplicable => true,
            Accessibility.Internal
                or Accessibility.ProtectedOrInternal =>
                SymbolEqualityComparer.Default.Equals(
                    constructor.ContainingAssembly, currentAssembly),
            _ => false,
        };

    private static bool HasAccessibleParameterlessConstructor(
        INamedTypeSymbol named, IAssemblySymbol currentAssembly)
        => named.InstanceConstructors.Any(ctor =>
            ctor.Parameters.Length == 0
            && (ctor.IsImplicitlyDeclared
                || IsAccessible(ctor, currentAssembly)));

    /// <summary>
    /// T6 empty site: an accessible instance constructor that can be invoked
    /// with no arguments — either genuinely parameterless, every parameter
    /// optional (<c>JsonArray(JsonNodeOptions? options = null)</c>), or a lone
    /// <c>params</c> parameter.
    /// </summary>
    private static bool TryFindNoRequiredArgumentConstructor(
        INamedTypeSymbol named,
        IAssemblySymbol currentAssembly,
        out IMethodSymbol? constructor)
    {
        foreach (var candidate in named.InstanceConstructors)
        {
            if (!IsConstructibleConstructorAccessible(candidate, currentAssembly)
                || candidate.Parameters.Length > 0
                   && !candidate.Parameters.All(static parameter =>
                       parameter.IsOptional || parameter.HasExplicitDefaultValue)
                   && !(candidate.Parameters.Length == 1
                        && candidate.Parameters[0].IsParams))
            {
                continue;
            }

            constructor = candidate;
            return true;
        }

        constructor = null;
        return false;
    }

    /// <summary>
    /// T6 non-empty site: an accessible instance constructor with exactly one
    /// parameter that is a rank-1 array (<c>T[]</c>, params or plain) or
    /// <c>ReadOnlySpan&lt;T&gt;</c> (params or plain). The element type is the
    /// parameter's element type; the emitter passes a <c>new TElem[]{ ... }</c>
    /// argument and relies on the array→ReadOnlySpan implicit conversion.
    /// </summary>
    private static bool TryFindConstructibleElementsConstructor(
        INamedTypeSymbol named,
        IAssemblySymbol currentAssembly,
        out IMethodSymbol? constructor,
        out ITypeSymbol? elementType)
    {
        constructor = null;
        elementType = null;

        foreach (var candidate in named.InstanceConstructors)
        {
            if (candidate.Parameters.Length != 1
                || !IsConstructibleConstructorAccessible(
                    candidate, currentAssembly))
            {
                continue;
            }

            var parameter = candidate.Parameters[0];
            var candidateElement = parameter.Type switch
            {
                IArrayTypeSymbol { Rank: 1 } array => array.ElementType,
                INamedTypeSymbol span
                    when span.OriginalDefinition.ToDisplayString()
                         == "System.ReadOnlySpan<T>"
                         && span.TypeArguments is { Length: 1 } arguments
                    => arguments[0],
                _ => null,
            };
            if (IsMissingOrError(candidateElement))
                continue;

            // Prefer the params array shape: a new T[] argument binds to it by
            // identity (a params ReadOnlySpan<T> overload would bind through
            // the array→span conversion). Element type and order are unchanged.
            if (parameter.IsParams && parameter.Type is IArrayTypeSymbol)
            {
                constructor = candidate;
                elementType = candidateElement;
                return true;
            }

            if (constructor == null)
            {
                constructor = candidate;
                elementType = candidateElement;
            }
        }

        return constructor != null;
    }

    /// <summary>
    /// Finds the collection-pattern Add: an accessible, non-generic instance
    /// method named Add with exactly one parameter whose type implicitly
    /// accepts every element the site feeds. Returns the parameter type too
    /// (the target element type for T3).
    /// </summary>
    private static bool TryFindPatternAdd(
        CSharpCompilation compilation,
        INamedTypeSymbol named,
        IAssemblySymbol currentAssembly,
        IReadOnlyList<ITypeSymbol?> elementFedTypes,
        out IMethodSymbol? addMethod,
        out ITypeSymbol? elementType)
    {
        addMethod = null;
        elementType = null;

        foreach (var method in EnumerateMethods(named, "Add"))
        {
            if (method.IsStatic
                || method.IsGenericMethod
                || method.Parameters.Length != 1
                || !IsAccessible(method, currentAssembly))
            {
                continue;
            }

            var parameterType = method.Parameters[0].Type;
            if (elementFedTypes.All(
                    fedType => ImplicitlyAccepts(compilation, fedType, parameterType)))
            {
                addMethod = method;
                elementType = parameterType;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="source"/> values implicitly convert to
    /// <paramref name="destination"/>. A null source type (undecidable
    /// operation type) is accepted — the input compilation itself has already
    /// validated the site, so eligibility is not rejected on metadata gaps.
    /// </summary>
    public static bool ImplicitlyAccepts(
        CSharpCompilation compilation,
        ITypeSymbol? source,
        ITypeSymbol destination)
        => source == null
           || compilation.ClassifyConversion(source, destination).IsImplicit;

    /// <summary>
    /// True when <paramref name="named"/> implements the non-generic
    /// <c>System.Collections.IEnumerable</c> marker (the collection
    /// initializer / collection-expression constructability requirement).
    /// </summary>
    private static bool ImplementsNonGenericEnumerable(INamedTypeSymbol named)
        => named.SpecialType == SpecialType.System_Collections_IEnumerable
           || named.AllInterfaces.Any(i =>
               i.SpecialType == SpecialType.System_Collections_IEnumerable);

    /// <summary>
    /// Enumerates named methods of <paramref name="named"/> and every base
    /// type in its chain (collection-pattern Add is commonly inherited).
    /// </summary>
    public static IEnumerable<IMethodSymbol> EnumerateMethods(
        INamedTypeSymbol named, string methodName)
    {
        for (var current = named; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(methodName))
            {
                if (member is IMethodSymbol method)
                    yield return method;
            }
        }
    }
}
