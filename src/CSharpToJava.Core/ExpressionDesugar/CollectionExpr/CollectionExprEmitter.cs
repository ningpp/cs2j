using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Result of a spread-bearing site's statement lowering: the declaration +
/// Add/AddRange/foreach statements, the reference expression that replaces the
/// site (<c>__ed_n.ToArray()</c> for array/Span targets; <c>__ed_n</c> for
/// concrete targets) and the per-spread expansion buckets for statistics.
/// </summary>
internal sealed record StatementEmission(
    IReadOnlyList<StatementSyntax> Statements,
    ExpressionSyntax Reference,
    IReadOnlyList<SpreadExpansion> Expansions);

/// <summary>
/// Emission for C36 (CE-2/3/7; design §4):
/// <list type="bullet">
/// <item>zero-temp array: <c>new T[]{ e0, e1 }</c>, empty site → <c>new T[]{}</c>;</item>
/// <item>zero-temp mutable concrete: <c>new global::C&lt;T&gt;(){ e0, e1 }</c>,
/// empty site → <c>new global::C&lt;T&gt;()</c> (no initializer);</item>
/// <item>spread-bearing: <c>var __ed_n = new ...(){}</c> plus strict
/// source-order Add / AddRange / foreach-Add; array targets close with
/// ToArray().</item>
/// </list>
/// Type syntax is always rendered from symbols via
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> (global-qualified,
/// no using dependency). Every element is emitted explicitly — OfType
/// filtering is forbidden. Constant folding is never performed.
/// </summary>
internal static class CollectionExprEmitter
{
    private const string ListOfTMetadataName = "System.Collections.Generic.List`1";

    /// <summary>Parses a global-qualified type name rendered from a symbol.</summary>
    public static TypeSyntax GlobalType(ITypeSymbol type)
        => SyntaxFactory.ParseTypeName(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    /// <summary>CE-2: <c>new T[]{ e0, ... }</c> (empty elements → <c>new T[]{}</c>).</summary>
    public static ExpressionSyntax BuildZeroTempArray(
        ITypeSymbol elementType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var arrayType = SyntaxFactory.ArrayType(
            GlobalType(elementType),
            SyntaxFactory.SingletonList(
                SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.OmittedArraySizeExpression()))));

        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.ArrayInitializerExpression,
            SyntaxFactory.SeparatedList(elements));

        return SyntaxFactory.ArrayCreationExpression(arrayType, initializer);
    }

    /// <summary>
    /// CE-3: <c>new global::C&lt;T&gt;(){ e0, ... }</c>; an empty site emits
    /// <c>new global::C&lt;T&gt;()</c> without an initializer.
    /// </summary>
    public static ExpressionSyntax BuildZeroTempCollection(
        INamedTypeSymbol targetType,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        var creation = SyntaxFactory.ObjectCreationExpression(GlobalType(targetType))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        if (elements.Count > 0)
        {
            creation = creation.WithInitializer(
                SyntaxFactory.InitializerExpression(
                    SyntaxKind.CollectionInitializerExpression,
                    SyntaxFactory.SeparatedList(elements)));
        }

        return creation;
    }

    /// <summary>
    /// CE-5/6/7: builds the temp declaration plus the strict source-order
    /// statement sequence for a spread-bearing site.
    /// </summary>
    /// <param name="model">semantic model (original tree) for symbol lookups.</param>
    /// <param name="plan">site plan.</param>
    /// <param name="visitedElements">elements of the visited collection node (1:1 with the plan).</param>
    /// <param name="tempName">allocated <c>__ed_n</c> collection temp.</param>
    /// <param name="allocateLoopVariable">allocator for foreach variables.</param>
    public static StatementEmission BuildStatementSequence(
        SemanticModel model,
        CollectionExprPlan plan,
        IReadOnlyList<CollectionElementSyntax> visitedElements,
        string tempName,
        Func<string> allocateLoopVariable)
    {
        var tempId = SyntaxFactory.IdentifierName(tempName);
        var statements = new List<StatementSyntax>();
        var expansions = new List<SpreadExpansion>();

        // var __ed_n = new List<T>(){};   (array/Span target path)
        // var __ed_n = new global::C<T>(); (concrete target path)
        ExpressionSyntax tempCreation;
        if (plan.EmitsArrayForm)
        {
            var listDefinition = model.Compilation
                .GetTypeByMetadataName(ListOfTMetadataName)!;
            var listType = listDefinition.Construct(plan.TargetElementType!);
            tempCreation = SyntaxFactory.ObjectCreationExpression(GlobalType(listType))
                .WithArgumentList(SyntaxFactory.ArgumentList())
                .WithInitializer(
                    SyntaxFactory.InitializerExpression(
                        SyntaxKind.CollectionInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>()));
        }
        else
        {
            tempCreation = SyntaxFactory.ObjectCreationExpression(
                    GlobalType((INamedTypeSymbol)plan.ConvertedType!))
                .WithArgumentList(SyntaxFactory.ArgumentList());
        }

        statements.Add(ExpressionDesugarTempLifter.DeclareVar(tempName, tempCreation));

        for (var i = 0; i < plan.Elements.Count; i++)
        {
            var elementPlan = plan.Elements[i];

            if (elementPlan.Kind == CollectionElementKind.Expression)
            {
                var expression = ((ExpressionElementSyntax)visitedElements[i]).Expression;
                statements.Add(SyntaxFactory.ExpressionStatement(
                    SpreadExpander.MemberInvoke(tempId, "Add", expression)));
                continue;
            }

            // Spread: the visited source expression is inlined exactly once.
            var spreadSyntax = (SpreadElementSyntax)visitedElements[i];
            var source = spreadSyntax.Expression;
            var sourceKind = elementPlan.Spread!.SourceKind;
            var sourceInfo = model.GetTypeInfo(elementPlan.Spread.Syntax.Expression);
            var sourceType = sourceInfo.ConvertedType ?? sourceInfo.Type;

            // List<T> (array target) always has AddRange; concrete targets
            // only when a symbol-compatible AddRange exists.
            var addRangeAvailable = plan.EmitsArrayForm
                || (plan.ConvertedType is INamedTypeSymbol concrete
                    && sourceType != null
                    && SpreadExpander.HasCompatibleAddRange(model, concrete, sourceType));

            var (spreadStatements, expansion) = SpreadExpander.BuildExpansion(
                sourceKind, source, tempId, allocateLoopVariable, addRangeAvailable);
            statements.AddRange(spreadStatements);
            expansions.Add(expansion);
        }

        ExpressionSyntax reference = plan.EmitsArrayForm
            ? SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    tempId,
                    SyntaxFactory.IdentifierName("ToArray")),
                SyntaxFactory.ArgumentList())
            : tempId;

        return new StatementEmission(statements, reference, expansions);
    }

    /// <summary>Extracts ordinary element expressions from a visited zero-temp site.</summary>
    public static IReadOnlyList<ExpressionSyntax> ExtractExpressions(
        IReadOnlyList<CollectionElementSyntax> visitedElements)
    {
        var expressions = new ExpressionSyntax[visitedElements.Count];
        for (var i = 0; i < visitedElements.Count; i++)
            expressions[i] = ((ExpressionElementSyntax)visitedElements[i]).Expression;
        return expressions;
    }
}
