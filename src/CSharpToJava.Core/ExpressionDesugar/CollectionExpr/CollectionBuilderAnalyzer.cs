using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// A uniquely resolved builder factory plus the collection element type its
/// single parameter carries (the <c>T</c> of <c>ReadOnlySpan&lt;T&gt;</c> /
/// <c>T[]</c>). <see cref="Factory"/> is already constructed when the source
/// method is generic (e.g. <c>ImmutableArray.Create&lt;T&gt;</c> style).
/// </summary>
internal sealed record ResolvedBuilderFactory(
    IMethodSymbol Factory,
    ITypeSymbol ElementType,
    bool ParameterIsReadOnlySpan);

/// <summary>
/// Reads <c>System.Runtime.CompilerServices.CollectionBuilderAttribute</c>
/// metadata and resolves the builder factory method by attribute metadata +
/// symbol analysis (M2, design §4 open decision 1: generic, not whitelisted).
/// The M0 probe (plan §6.3) proved the compiler-bound Create invocation never
/// enters the IOperation tree on Roslyn 5.0, so resolution is fully
/// symbol-driven here.
/// </summary>
internal static class CollectionBuilderAnalyzer
{
    public const string AttributeFullName =
        "System.Runtime.CompilerServices.CollectionBuilderAttribute";

    private const string ReadOnlySpanOfT = "System.ReadOnlySpan<T>";
    private const string IEnumerableOfT = "System.Collections.Generic.IEnumerable<T>";

    /// <summary>
    /// Reads the two attribute constructor arguments:
    /// <c>[CollectionBuilder(typeof(TBuilder), "Create")]</c>.
    /// Returns false (with nulls) when the attribute is absent or incomplete.
    /// </summary>
    public static bool TryGetBuilder(
        INamedTypeSymbol targetType,
        out INamedTypeSymbol? builderType,
        out string? factoryMethodName)
    {
        builderType = null;
        factoryMethodName = null;

        foreach (var attribute in targetType.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != AttributeFullName)
                continue;

            var args = attribute.ConstructorArguments;
            if (args.Length >= 1)
                builderType = args[0].Value as INamedTypeSymbol;
            if (args.Length >= 2)
                factoryMethodName = args[1].Value as string;

            return builderType != null && factoryMethodName != null;
        }

        return false;
    }

    /// <summary>
    /// M2: resolves the UNIQUE static builder factory method named
    /// <paramref name="factoryMethodName"/> on <paramref name="builderType"/>.
    /// Accepted candidate shapes (symbol-verified, never guessed):
    /// <list type="bullet">
    /// <item>single parameter <c>ReadOnlySpan&lt;X&gt;</c> (the compiler's
    /// required shape — the emitted <c>new X[]{...}</c> converts implicitly),
    /// or defensively <c>X[]</c> / <c>params X[]</c>;</item>
    /// <item>non-generic, or generic with exactly one type parameter bound to
    /// the target collection's element type (<c>Create&lt;T&gt;</c> style);</item>
    /// <item>return type implicitly convertible to the target type;</item>
    /// <item>every element fed at the site implicitly converts to X.</item>
    /// </list>
    /// ReadOnlySpan candidates win over array/params candidates (the compiler
    /// binds exactly one ROS-shaped method on compilable input; array/params
    /// shapes themselves draw baseline CS9187 and exist only as a defensive
    /// superset). Zero candidates → no-factory error; multiple same-rank
    /// candidates → ambiguity error. Both keep the site on CS2J0580.
    /// </summary>
    public static bool TryResolveFactory(
        CSharpCompilation compilation,
        INamedTypeSymbol targetType,
        INamedTypeSymbol builderType,
        string factoryMethodName,
        IReadOnlyList<ITypeSymbol?> elementFedTypes,
        out ResolvedBuilderFactory? factory,
        out string? error)
    {
        factory = null;
        error = null;

        var builderDisplay = builderType.ToDisplayString();

        if (builderType.TypeArguments.Any(a => a is ITypeParameterSymbol))
        {
            error = $"builder type '{builderDisplay}' is an open generic type; " +
                    "its type arguments cannot be inferred from the attribute";
            return false;
        }

        var targetElement = TryGetTargetElementType(targetType);

        var rosCandidates = new List<ResolvedBuilderFactory>();
        var arrayCandidates = new List<ResolvedBuilderFactory>();

        foreach (var method in TargetTypeResolver.EnumerateMethods(builderType, factoryMethodName))
        {
            if (!method.IsStatic
                || !TargetTypeResolver.IsAccessible(method, compilation.Assembly)
                || method.Parameters.Length != 1
                || method.Parameters[0].RefKind != RefKind.None
                || method.IsVararg)
            {
                continue;
            }

            if (method.IsGenericMethod)
            {
                if (method.TypeParameters.Length != 1 || targetElement == null)
                    continue;

                var constructed = method.Construct(targetElement);
                if (TryMatch(compilation, constructed, targetType, targetElement,
                        elementFedTypes, out var ros, out var matched))
                {
                    (ros ? rosCandidates : arrayCandidates).Add(matched!);
                }
            }
            else
            {
                if (TryMatch(compilation, method, targetType, targetElement,
                        elementFedTypes, out var ros, out var matched))
                {
                    (ros ? rosCandidates : arrayCandidates).Add(matched!);
                }
            }
        }

        var candidates = rosCandidates.Count > 0 ? rosCandidates : arrayCandidates;
        var rankName = rosCandidates.Count > 0
            ? "ReadOnlySpan<T>"
            : "T[]/params T[]";

        if (candidates.Count == 1)
        {
            factory = candidates[0];
            return true;
        }

        error = candidates.Count == 0
            ? $"no accessible static method '{factoryMethodName}' with a single " +
              $"ReadOnlySpan<T> (or T[]/params T[]) parameter whose result converts " +
              $"to the target type was found on builder type '{builderDisplay}' " +
              $"(baseline CS9187 shape)"
            : $"multiple applicable static '{factoryMethodName}' factory methods " +
              $"({rankName}) on builder type '{builderDisplay}' are ambiguous; " +
              "the unique factory cannot be resolved";
        return false;
    }

    /// <summary>
    /// Matches one concrete (already type-constructed) factory method:
    /// extracts X from its single parameter, checks the return conversion to
    /// the target, element identity with the target's element type and every
    /// fed element's implicit conversion.
    /// </summary>
    private static bool TryMatch(
        CSharpCompilation compilation,
        IMethodSymbol method,
        INamedTypeSymbol targetType,
        ITypeSymbol? targetElement,
        IReadOnlyList<ITypeSymbol?> elementFedTypes,
        out bool isReadOnlySpan,
        out ResolvedBuilderFactory? matched)
    {
        isReadOnlySpan = false;
        matched = null;

        var parameterType = method.Parameters[0].Type;
        ITypeSymbol? elementType = null;

        if (parameterType is INamedTypeSymbol parameterNamed
            && parameterNamed.IsGenericType
            && parameterNamed.OriginalDefinition.ToDisplayString() == ReadOnlySpanOfT
            && parameterNamed.TypeArguments is { Length: 1 } rosArgs)
        {
            elementType = rosArgs[0];
            isReadOnlySpan = true;
        }
        else if (parameterType is IArrayTypeSymbol { Rank: 1 } arrayParameter)
        {
            elementType = arrayParameter.ElementType;
        }

        if (elementType == null || elementType.Kind == SymbolKind.ErrorType)
            return false;

        // The factory result must be usable where the target is expected.
        if (!compilation.ClassifyConversion(method.ReturnType, targetType).IsImplicit
            && !SymbolEqualityComparer.Default.Equals(method.ReturnType, targetType))
        {
            return false;
        }

        // The parameter element type must agree with the collection's own
        // element type (its IEnumerable<T> implementation).
        if (targetElement != null
            && !SymbolEqualityComparer.Default.Equals(elementType, targetElement))
        {
            return false;
        }

        // Every element actually fed at the site converts to X.
        if (elementFedTypes.Any(
                fedType => !TargetTypeResolver.ImplicitlyAccepts(
                    compilation, fedType, elementType)))
        {
            return false;
        }

        matched = new ResolvedBuilderFactory(method, elementType, isReadOnlySpan);
        return true;
    }

    /// <summary>
    /// Element type of a builder target: the T of the
    /// <c>IEnumerable&lt;T&gt;</c> it implements (the CS9188 element type),
    /// checked on the target itself first, then its interfaces.
    /// </summary>
    private static ITypeSymbol? TryGetTargetElementType(INamedTypeSymbol targetType)
    {
        var candidates = new[] { targetType }.Concat(targetType.AllInterfaces);
        foreach (var candidate in candidates)
        {
            if (candidate is { IsGenericType: true }
                && candidate.OriginalDefinition.ToDisplayString() == IEnumerableOfT
                && candidate.TypeArguments is { Length: 1 } enumArgs
                && enumArgs[0].Kind != SymbolKind.ErrorType)
            {
                return enumArgs[0];
            }
        }

        return null;
    }

    /// <summary>
    /// M0 seam (documentary): on Roslyn 5.0 the builder factory invocation is
    /// NOT lowered into the IOperation tree — <see cref="TryResolveFactory"/>
    /// is the M2 path. Kept to lock the negative probe result.
    /// </summary>
    public static IInvocationOperation? FindBoundFactoryInvocation(
        IOperation root,
        INamedTypeSymbol builderType,
        string factoryMethodName)
    {
        var original = builderType.OriginalDefinition;
        foreach (var operation in root.Descendants())
        {
            if (operation is not IInvocationOperation invocation)
                continue;

            var method = invocation.TargetMethod;
            if (method == null
                || !string.Equals(method.Name, factoryMethodName, StringComparison.Ordinal)
                || method.ContainingType?.OriginalDefinition is not { } containing)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(containing, original)
                || SymbolEqualityComparer.Default.Equals(
                    containing.ConstructedFrom, builderType.ConstructedFrom))
            {
                return invocation;
            }
        }

        return null;
    }
}
