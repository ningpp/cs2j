using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles LINQ query expressions.
/// </summary>
public class QueryExpressionTransformer : IExpressionTransformer
{
    static QueryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.QueryExpression
        }, new QueryExpressionTransformer());
    }

    private static readonly Lazy<QueryExpressionTransformer> _instance = new(() => new());
    public static QueryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.QueryExpression => TransformQuery((QueryExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Query expression kind {node.Kind()} not supported.")
        };

    private string TransformQuery(QueryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var sb = new System.Text.StringBuilder();

        // from clause — seed the stream
        var fromClause = node.FromClause;
        var rangeVar = ConversionContext.EscapeJavaKeyword(fromClause.Identifier.Text);
        var source = facade.Transform(fromClause.Expression, context);
        sb.Append($"{source}.stream()");

        // intermediate clauses
        foreach (var clause in node.Body.Clauses)
        {
            switch (clause)
            {
                case WhereClauseSyntax where:
                    var cond = facade.Transform(where.Condition, context);
                    sb.Append($"\n    .filter({rangeVar} -> {cond})");
                    break;

                case OrderByClauseSyntax orderBy:
                    foreach (var ordering in orderBy.Orderings)
                    {
                        var keyExpr = facade.Transform(ordering.Expression, context);
                        var desc = ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                        sb.Append($"\n    .sorted(java.util.Comparator.comparing({rangeVar} -> {keyExpr}){desc})");
                    }
                    break;

                case FromClauseSyntax additionalFrom:
                    // nested from → flatMap; range variable shifts to inner
                    var innerVar = ConversionContext.EscapeJavaKeyword(additionalFrom.Identifier.Text);
                    var innerSrc = facade.Transform(additionalFrom.Expression, context);
                    sb.Append($"\n    .flatMap({rangeVar} -> {innerSrc}.stream())");
                    rangeVar = innerVar;
                    break;

                case LetClauseSyntax let:
                    // let x = expr is hard to represent as a stream step; defer to InvocationExpressionTransformer
                    var letVar = ConversionContext.EscapeJavaKeyword(let.Identifier.Text);
                    var letExpr = facade.Transform(let.Expression, context);
                    if (context.QueryLetAliases != null)
                        context.QueryLetAliases[letVar] = letExpr;
                    break;

                case JoinClauseSyntax:
                    sb.Append($"\n    /* TODO: join clause */");
                    break;
            }
        }

        // terminal: select or group
        switch (node.Body.SelectOrGroup)
        {
            case SelectClauseSyntax select:
                var selectExpr = facade.Transform(select.Expression, context);
                if (selectExpr != rangeVar)
                    sb.Append($"\n    .map({rangeVar} -> {selectExpr})");
                sb.Append("\n    .collect(java.util.stream.Collectors.toList())");
                context.AddImport("java.util.stream.Collectors");
                break;

            case GroupClauseSyntax group:
                var groupExpr = facade.Transform(group.GroupExpression, context);
                var byExpr = facade.Transform(group.ByExpression, context);
                if (groupExpr != rangeVar)
                    sb.Append($"\n    .map({rangeVar} -> {groupExpr})");
                sb.Append($"\n    .collect(java.util.stream.Collectors.groupingBy({rangeVar} -> {byExpr}))");
                context.AddImport("java.util.stream.Collectors");
                break;
        }

        return sb.ToString();
    }
}
