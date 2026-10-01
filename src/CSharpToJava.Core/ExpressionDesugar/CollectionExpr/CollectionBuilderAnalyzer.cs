using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Reads <c>System.Runtime.CompilerServices.CollectionBuilderAttribute</c>
/// metadata and (M2) the compiler-bound builder factory invocation in the
/// IOperation tree (design §4, open question 1: generic, IOperation-driven).
/// </summary>
internal static class CollectionBuilderAnalyzer
{
    public const string AttributeFullName =
        "System.Runtime.CompilerServices.CollectionBuilderAttribute";

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
    /// M2 seam: locates the invocation the compiler bound to the builder
    /// factory method (e.g. <c>MyBuilder.Create(new ReadOnlySpan&lt;T&gt;(...))</c>).
    /// M0 ships the generic scan so the probe tests can lock the exact shape;
    /// matching is on containing type (original definition) + method name.
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
