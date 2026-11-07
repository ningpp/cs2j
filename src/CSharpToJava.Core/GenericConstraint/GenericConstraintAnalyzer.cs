using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericConstraint;

/// <summary>
/// Whole-tree facts the generic-constraint pass needs across files: which type
/// parameters have at least one CN-dependent site (CN-01/03/04/07/09/10/11).
/// CN-12 only warns for an unexpressible constraint that has NO dependent site
/// anywhere in the tree, so a file-local walk is not enough.
/// </summary>
public sealed class GenericConstraintAnalysis
{
    public static GenericConstraintAnalysis Empty { get; } = new(
        new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default));

    public GenericConstraintAnalysis(IReadOnlySet<ITypeParameterSymbol> dependentParameters)
    {
        DependentParameters = dependentParameters;
    }

    /// <summary>Type parameters referenced by at least one CN-dependent site.</summary>
    public IReadOnlySet<ITypeParameterSymbol> DependentParameters { get; }
}

/// <summary>
/// Read-only whole-compilation scan feeding <see cref="GenericConstraintAnalysis"/>.
/// It never rewrites and never emits diagnostics; the rewriter owns both.
/// </summary>
public static class GenericConstraintAnalyzer
{
    public static GenericConstraintAnalysis Analyze(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var dependent = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel model;
            try
            {
                model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case DefaultExpressionSyntax defaultExpression:
                        MarkTypeParameter(model, defaultExpression.Type, dependent);
                        break;

                    case LiteralExpressionSyntax literal
                        when literal.IsKind(SyntaxKind.DefaultLiteralExpression):
                        MarkConvertedTypeParameter(model, literal, dependent);
                        break;

                    case NullableTypeSyntax nullable:
                        MarkTypeParameter(model, nullable.ElementType, dependent);
                        break;

                    case ObjectCreationExpressionSyntax creation:
                        MarkTypeParameter(model, creation.Type, dependent);
                        break;

                    case ArrayCreationExpressionSyntax array:
                        MarkTypeParameter(model, InnermostElementType(array.Type), dependent);
                        break;

                    case TypeOfExpressionSyntax typeOf:
                        MarkTypeParameter(model, typeOf.Type, dependent);
                        break;

                    case CastExpressionSyntax cast
                        when cast.Type is PredefinedTypeSyntax predefined
                             && predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword):
                        MarkExpressionTypeParameter(model, cast.Expression, dependent);
                        break;

                    case BinaryExpressionSyntax binary
                        when binary.IsKind(SyntaxKind.IsExpression)
                             || binary.IsKind(SyntaxKind.AsExpression):
                        if (binary.Right is TypeSyntax rightType)
                        {
                            MarkTypeParameter(model, rightType, dependent);
                        }
                        break;

                    case InvocationExpressionSyntax invocation:
                        MarkInvocationTypeArguments(model, invocation, dependent);
                        break;
                }
            }
        }

        return new GenericConstraintAnalysis(dependent);
    }

    private static void MarkInvocationTypeArguments(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        HashSet<ITypeParameterSymbol> dependent)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || member.Name is not GenericNameSyntax genericName)
        {
            return;
        }

        IMethodSymbol? method;
        try
        {
            method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        }
        catch (ArgumentException)
        {
            return;
        }

        if (method == null)
        {
            // CN-10 syntax fallback: Delegate.Combine/Remove<T> has no generic
            // BCL member on every TFM, but the dependent site still counts.
            if (IsSyntacticDelegateReceiver(member))
            {
                MarkTypeArguments(model, genericName, dependent);
            }

            return;
        }

        var containingType = method.ContainingType;
        if (containingType == null
            || (containingType.SpecialType != SpecialType.System_Enum
                && containingType.SpecialType != SpecialType.System_Delegate))
        {
            return;
        }

        MarkTypeArguments(model, genericName, dependent);
    }

    private static void MarkTypeArguments(
        SemanticModel model,
        GenericNameSyntax genericName,
        HashSet<ITypeParameterSymbol> dependent)
    {
        foreach (var argument in genericName.TypeArgumentList.Arguments)
        {
            MarkTypeParameter(model, argument, dependent);
        }
    }

    private static bool IsSyntacticDelegateReceiver(MemberAccessExpressionSyntax member)
    {
        if (member.Name is not GenericNameSyntax { Identifier.ValueText: "Combine" or "Remove" })
        {
            return false;
        }

        var text = member.Expression.WithoutTrivia().ToString().Replace(" ", string.Empty);
        return text is "Delegate" or "System.Delegate" or "global::System.Delegate";
    }

    private static void MarkTypeParameter(
        SemanticModel model,
        TypeSyntax? type,
        HashSet<ITypeParameterSymbol> dependent)
    {
        if (type == null)
        {
            return;
        }

        try
        {
            var parameter = model.GetSymbolInfo(type).Symbol as ITypeParameterSymbol
                ?? model.GetTypeInfo(type).Type as ITypeParameterSymbol;
            if (parameter != null)
            {
                dependent.Add(parameter);
            }
        }
        catch (ArgumentException)
        {
            // Node not part of this model's tree (stale model); undecided.
        }
    }

    private static void MarkConvertedTypeParameter(
        SemanticModel model,
        LiteralExpressionSyntax literal,
        HashSet<ITypeParameterSymbol> dependent)
    {
        try
        {
            if (model.GetTypeInfo(literal).ConvertedType is ITypeParameterSymbol parameter)
            {
                dependent.Add(parameter);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static void MarkExpressionTypeParameter(
        SemanticModel model,
        ExpressionSyntax expression,
        HashSet<ITypeParameterSymbol> dependent)
    {
        try
        {
            if (model.GetTypeInfo(expression).Type is ITypeParameterSymbol parameter)
            {
                dependent.Add(parameter);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static TypeSyntax InnermostElementType(ArrayTypeSyntax array)
    {
        var element = array.ElementType;
        while (element is ArrayTypeSyntax nested)
        {
            element = nested.ElementType;
        }

        return element;
    }
}
