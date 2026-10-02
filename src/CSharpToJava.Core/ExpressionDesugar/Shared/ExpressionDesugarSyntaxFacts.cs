using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Shared syntax/semantic facts for the expression-desugar family.
/// </summary>
internal static class ExpressionDesugarSyntaxFacts
{
    /// <summary>Marks a trigger node intentionally accounted for (skip/diagnostic-only).</summary>
    public static readonly SyntaxAnnotation ExplainedAnnotation =
        new("ed-explained");

    /// <summary>Marks a subtree emitted by a rule (nested triggers inside are explained).</summary>
    public static readonly SyntaxAnnotation ExplainedSubtreeAnnotation =
        new("ed-explained-subtree");

    private const string ExpressionTreePrefix =
        "global::System.Linq.Expressions.Expression<";

    /// <summary>
    /// True when <paramref name="node"/> is syntactically inside a lambda that
    /// converts to <c>System.Linq.Expressions.Expression&lt;T&gt;</c>. The
    /// semantic criterion mirrors the repository-wide
    /// <c>Analysis.ExpressionTreeFacts</c> 口径 (converted-type prefix), applied
    /// to any node by walking its enclosing lambdas.
    /// </summary>
    public static bool IsInExpressionTree(SemanticModel? model, SyntaxNode node)
    {
        if (model == null)
            return false;

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is not LambdaExpressionSyntax lambda)
                continue;

            var convertedType = model.GetTypeInfo(lambda).ConvertedType;
            if (convertedType == null)
                continue;

            var fullName = convertedType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName.StartsWith(ExpressionTreePrefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when statement sequences can be lifted at <paramref name="node"/>:
    /// an enclosing statement anchor exists (pre-insert / embedded-block wrap)
    /// or an expression-bodied clause can be expanded. Field/property/event
    /// initializers, attribute arguments, default parameter values and query
    /// clauses are anchor-free (design §2.4 可写上下文).
    /// </summary>
    public static bool IsInLiftableContext(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            switch (current)
            {
                case StatementSyntax:
                case ArrowExpressionClauseSyntax:
                    return true;

                // Anchor-free contexts (checked outer-first).
                case FieldDeclarationSyntax:
                case EventFieldDeclarationSyntax:
                case PropertyDeclarationSyntax { Initializer: not null }:
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                // A default parameter value is reached via ParameterSyntax.
                case ParameterSyntax:
                case QueryClauseSyntax:
                    return false;
            }

            current = current.Parent;
        }

        return false;
    }
}
