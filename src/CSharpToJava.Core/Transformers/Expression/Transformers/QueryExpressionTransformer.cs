using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles LINQ query expressions.
/// </summary>
[TransformerRegistration]
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
        // Fix 4: arrays don't have .stream(); use Arrays.stream() instead
        bool isArraySource = context.SemanticModel?.GetTypeInfo(fromClause.Expression).Type is IArrayTypeSymbol;
        if (isArraySource) context.AddImport("java.util.Arrays");
        sb.Append(isArraySource ? $"Arrays.stream({source})" : $"{source}.stream()");

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
                    // Fix 6: chain multiple sort keys into a single Comparator
                    var orderings = orderBy.Orderings;
                    var firstKey = facade.Transform(orderings[0].Expression, context);
                    var firstDesc = orderings[0].AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                    var comparatorBuilder = new System.Text.StringBuilder($"java.util.Comparator.comparing({rangeVar} -> {firstKey}){firstDesc}");
                    for (int oi = 1; oi < orderings.Count; oi++)
                    {
                        var ord = orderings[oi];
                        var thenKey = facade.Transform(ord.Expression, context);
                        var thenDesc = ord.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                        comparatorBuilder.Append($"\n        .thenComparing({rangeVar} -> {thenKey}){thenDesc}");
                    }
                    sb.Append($"\n    .sorted({comparatorBuilder})");
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

                case JoinClauseSyntax join:
                    // Fix 1: implement join via flatMap + filter on equality
                    var joinVar = ConversionContext.EscapeJavaKeyword(join.Identifier.Text);
                    var joinInExpr = facade.Transform(join.InExpression, context);
                    var joinLeftExpr = facade.Transform(join.LeftExpression, context);
                    var joinRightExpr = facade.Transform(join.RightExpression, context);
                    sb.Append($"\n    .flatMap({rangeVar} -> {joinInExpr}.stream()" +
                              $"\n        .filter({joinVar} -> java.util.Objects.equals({joinLeftExpr}, {joinRightExpr})))");
                    rangeVar = joinVar;
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
                // Fix 3: when groupExpr != rangeVar, use Collectors.mapping instead of a
                // preceding .map() so both lambdas close over the correct original rangeVar
                if (groupExpr != rangeVar)
                    sb.Append($"\n    .collect(java.util.stream.Collectors.groupingBy({rangeVar} -> {byExpr}," +
                              $" java.util.stream.Collectors.mapping({rangeVar} -> {groupExpr}, java.util.stream.Collectors.toList())))");
                else
                    sb.Append($"\n    .collect(java.util.stream.Collectors.groupingBy({rangeVar} -> {byExpr}))");
                context.AddImport("java.util.stream.Collectors");
                break;
        }

        // Fix 5: handle 'into' continuation
        if (node.Body.Continuation != null)
        {
            var cont = node.Body.Continuation;
            rangeVar = ConversionContext.EscapeJavaKeyword(cont.Identifier.Text);
            foreach (var contClause in cont.Body.Clauses)
            {
                switch (contClause)
                {
                    case WhereClauseSyntax contWhere:
                        var contCond = facade.Transform(contWhere.Condition, context);
                        sb.Append($"\n    .filter({rangeVar} -> {contCond})");
                        break;
                    case OrderByClauseSyntax contOrderBy:
                        var contOrderings = contOrderBy.Orderings;
                        var contFirstKey = facade.Transform(contOrderings[0].Expression, context);
                        var contFirstDesc = contOrderings[0].AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                        var contComparator = new System.Text.StringBuilder($"java.util.Comparator.comparing({rangeVar} -> {contFirstKey}){contFirstDesc}");
                        for (int ci = 1; ci < contOrderings.Count; ci++)
                        {
                            var cord = contOrderings[ci];
                            var ck = facade.Transform(cord.Expression, context);
                            var cd = cord.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                            contComparator.Append($"\n        .thenComparing({rangeVar} -> {ck}){cd}");
                        }
                        sb.Append($"\n    .sorted({contComparator})");
                        break;
                }
            }
            switch (cont.Body.SelectOrGroup)
            {
                case SelectClauseSyntax contSelect:
                    var contSelectExpr = facade.Transform(contSelect.Expression, context);
                    if (contSelectExpr != rangeVar)
                        sb.Append($"\n    .map({rangeVar} -> {contSelectExpr})");
                    sb.Append("\n    .collect(java.util.stream.Collectors.toList())");
                    context.AddImport("java.util.stream.Collectors");
                    break;
                case GroupClauseSyntax contGroup:
                    var contGroupExpr = facade.Transform(contGroup.GroupExpression, context);
                    var contByExpr = facade.Transform(contGroup.ByExpression, context);
                    if (contGroupExpr != rangeVar)
                        sb.Append($"\n    .collect(java.util.stream.Collectors.groupingBy({rangeVar} -> {contByExpr}," +
                                  $" java.util.stream.Collectors.mapping({rangeVar} -> {contGroupExpr}, java.util.stream.Collectors.toList())))");
                    else
                        sb.Append($"\n    .collect(java.util.stream.Collectors.groupingBy({rangeVar} -> {contByExpr}))");
                    context.AddImport("java.util.stream.Collectors");
                    break;
            }
        }

        // Clear let-alias mappings so they don't leak into subsequent code
        context.QueryLetAliases.Clear();

        return sb.ToString();
    }
}
