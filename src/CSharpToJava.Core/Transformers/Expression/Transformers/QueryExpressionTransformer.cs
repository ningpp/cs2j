using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;

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
        bool groupJoinHandled = false;

        try
        {
        // from clause — seed the stream
        var fromClause = node.FromClause;
        var rangeVar = ConversionContext.EscapeJavaKeyword(fromClause.Identifier.Text);
        var source = facade.Transform(fromClause.Expression, context);
        var fromClauseType = context.SemanticModel?.GetTypeInfo(fromClause.Expression).Type;
        sb.Append(ExpressionTransformerHelpers.BuildStreamExpression(source, fromClauseType, context, receiverSyntaxNode: fromClause.Expression));

        // intermediate clauses — use index loop for look-ahead on join…into
        var clauseList = node.Body.Clauses.ToList();
        for (int ci = 0; ci < clauseList.Count; ci++)
        {
            var clause = clauseList[ci];
            switch (clause)
            {
                case WhereClauseSyntax where:
                    var cond = facade.Transform(where.Condition, context);
                    sb.Append($"\n    .filter({rangeVar} -> {cond})");
                    break;

                case OrderByClauseSyntax orderBy:
                    // Fix 6: chain multiple sort keys into a single Comparator
                    context.AddImport("java.util.Comparator");
                    var orderings = orderBy.Orderings;
                    var orderLambdaParam = BuildTypedLambdaParameter(fromClauseType, rangeVar, context, orderings[0].Expression);
                    var firstKey = facade.Transform(orderings[0].Expression, context);
                    var firstDesc = orderings[0].AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                    var comparatorBuilder = new System.Text.StringBuilder($"Comparator.comparing({orderLambdaParam} -> {firstKey}){firstDesc}");
                    for (int oi = 1; oi < orderings.Count; oi++)
                    {
                        var ord = orderings[oi];
                        var thenKey = facade.Transform(ord.Expression, context);
                        var thenDesc = ord.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                        comparatorBuilder.Append($"\n        .thenComparing({orderLambdaParam} -> {thenKey}){thenDesc}");
                    }
                    sb.Append($"\n    .sorted({comparatorBuilder})");
                    break;

                case FromClauseSyntax additionalFrom:
                    // nested from → flatMap; range variable shifts to inner
                    var innerVar = ConversionContext.EscapeJavaKeyword(additionalFrom.Identifier.Text);
                    var innerSrc = facade.Transform(additionalFrom.Expression, context);
                    var innerSrcType = context.SemanticModel?.GetTypeInfo(additionalFrom.Expression).Type;
                    // If the current stream is a primitive stream (e.g. IntStream from int[]),
                    // we must .boxed() before .flatMap() which expects Function<T, Stream<R>>.
                    if (fromClauseType is IArrayTypeSymbol prevArr
                        && prevArr.ElementType.SpecialType is
                            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
                            or SpecialType.System_UInt32 or SpecialType.System_UInt16 or SpecialType.System_SByte
                            or SpecialType.System_Int64 or SpecialType.System_UInt64
                            or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal)
                    {
                        sb.Append("\n    .boxed()");
                    }
                    sb.Append($"\n    .flatMap({rangeVar} -> {ExpressionTransformerHelpers.BuildStreamExpression(innerSrc, innerSrcType, context)})");
                    rangeVar = innerVar;
                    break;

                case LetClauseSyntax let:
                    // let x = expr is hard to represent as a stream step; defer to InvocationExpressionTransformer
                    var letVar = ConversionContext.EscapeJavaKeyword(let.Identifier.Text);
                    var letExpr = facade.Transform(let.Expression, context);
                    if (context.QueryLetAliases != null)
                        context.QueryLetAliases[letVar] = letExpr;
                    break;

                case JoinClauseSyntax join when join.Into == null:
                    // Fix 1: regular equi-join via flatMap + filter on equality
                    context.AddImport("java.util.Objects");
                    var joinVar = ConversionContext.EscapeJavaKeyword(join.Identifier.Text);
                    var joinInExpr = facade.Transform(join.InExpression, context);
                    var joinInType = context.SemanticModel?.GetTypeInfo(join.InExpression).Type;
                    var joinLeftExpr = facade.Transform(join.LeftExpression, context);
                    var joinRightExpr = facade.Transform(join.RightExpression, context);
                    sb.Append($"\n    .flatMap({rangeVar} -> {ExpressionTransformerHelpers.BuildStreamExpression(joinInExpr, joinInType, context)}" +
                              $"\n        .filter({joinVar} -> Objects.equals({joinLeftExpr}, {joinRightExpr})))");
                    rangeVar = joinVar;
                    break;

                case JoinClauseSyntax joinInto when joinInto.Into != null:
                {
                    // GroupJoin pattern: join y in src on x equals y into g
                    // Typically followed by: from y in g.DefaultIfEmpty()
                    // Together these form a LEFT OUTER JOIN.
                    context.AddImport("java.util.Objects");
                    var capturedOuter = rangeVar;
                    var intoId = ConversionContext.EscapeJavaKeyword(joinInto.Into.Identifier.Text);
                    var jiVar = ConversionContext.EscapeJavaKeyword(joinInto.Identifier.Text);
                    var jiInSrc = facade.Transform(joinInto.InExpression, context);
                    var jiLeft = facade.Transform(joinInto.LeftExpression, context);
                    var jiRight = facade.Transform(joinInto.RightExpression, context);

                    // Look ahead: next clause should be "from y in g.DefaultIfEmpty()"
                    string innerLoopVar = jiVar;
                    if (ci + 1 < clauseList.Count && clauseList[ci + 1] is FromClauseSyntax fromGroup)
                    {
                        innerLoopVar = ConversionContext.EscapeJavaKeyword(fromGroup.Identifier.Text);
                        ci++;
                    }

                    // Collect any following inner-scope where clauses
                    var innerPipe = new System.Text.StringBuilder();
                    while (ci + 1 < clauseList.Count && clauseList[ci + 1] is WhereClauseSyntax innerWhere)
                    {
                        var innerCond2 = facade.Transform(innerWhere.Condition, context);
                        innerPipe.Append($".filter({innerLoopVar} -> {innerCond2})");
                        ci++;
                    }

                    // Resolve the terminal select expression so we can inline it in the flatMap
                    string terminalMapExpr = "";
                    if (node.Body.SelectOrGroup is SelectClauseSyntax finalSelect)
                    {
                        var termExpr = facade.Transform(finalSelect.Expression, context);
                        if (termExpr != innerLoopVar)
                            terminalMapExpr = termExpr;
                    }

                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    context.AddImport("java.util.Collections");
                    var jiInSrcType = context.SemanticModel?.GetTypeInfo(joinInto.InExpression).Type;
                    sb.Append($"\n    .flatMap({capturedOuter} -> {{");
                    sb.Append($"\n        var {intoId} = {ExpressionTransformerHelpers.BuildStreamExpression(jiInSrc, jiInSrcType, context)}");
                    sb.Append($"\n            .filter({jiVar} -> Objects.equals({jiLeft}, {jiRight}))");
                    sb.Append($"\n            .collect(Collectors.toCollection(() -> new ArrayList<>()));");
                    sb.Append($"\n        var _{intoId} = {intoId}.isEmpty() ? java.util.Collections.singletonList((Object)null) : {intoId};");
                    if (!string.IsNullOrEmpty(terminalMapExpr))
                        sb.Append($"\n        return _{intoId}.stream(){innerPipe}.map({innerLoopVar} -> {terminalMapExpr});");
                    else
                        sb.Append($"\n        return _{intoId}.stream(){innerPipe};");
                    sb.Append("\n    })");
                    groupJoinHandled = true;
                    ci = clauseList.Count; // skip remaining — terminal emitted inline
                    break;
                }
            }
        }

        if (!groupJoinHandled)
        {
            // terminal: select or group
            switch (node.Body.SelectOrGroup)
            {
                case SelectClauseSyntax select:
                    var selectExpr = facade.Transform(select.Expression, context);
                    if (selectExpr != rangeVar)
                        sb.Append($"\n    .map({rangeVar} -> {selectExpr})");
                    sb.Append("\n    .collect(Collectors.toCollection(() -> new ArrayList<>()))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    break;

                case GroupClauseSyntax group:
                    var groupExpr = facade.Transform(group.GroupExpression, context);
                    var byExpr = facade.Transform(group.ByExpression, context);
                    // Fix 3: when groupExpr != rangeVar, use Collectors.mapping instead of a
                    // preceding .map() so both lambdas close over the correct original rangeVar
                    if (groupExpr != rangeVar)
                        sb.Append($"\n    .collect(Collectors.groupingBy({rangeVar} -> {byExpr}," +
                                  $" Collectors.mapping({rangeVar} -> {groupExpr}, Collectors.toCollection(() -> new ArrayList<>()))))");
                    else
                        sb.Append($"\n    .collect(Collectors.groupingBy({rangeVar} -> {byExpr}))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    break;
            }
        }
        else
        {
            // GroupJoin emitted terminal inline; add the outer collect
            context.AddImport("java.util.stream.Collectors");
            context.AddImport("java.util.ArrayList");
            sb.Append("\n    .collect(Collectors.toCollection(() -> new ArrayList<>()))");
        }

        // Fix 5: handle 'into' continuation (not applicable after GroupJoin handling)
        if (node.Body.Continuation != null && !groupJoinHandled)
        {
            var cont = node.Body.Continuation;
            rangeVar = ConversionContext.EscapeJavaKeyword(cont.Identifier.Text);
            // The first query body above materializes to List via collect(toList()).
            // Continuation clauses (where/orderby/select) must continue from a stream.
            sb.Append("\n    .stream()");
            foreach (var contClause in cont.Body.Clauses)
            {
                switch (contClause)
                {
                    case WhereClauseSyntax contWhere:
                        var contCond = facade.Transform(contWhere.Condition, context);
                        sb.Append($"\n    .filter({rangeVar} -> {contCond})");
                        break;
                    case OrderByClauseSyntax contOrderBy:
                        context.AddImport("java.util.Comparator");
                        var contOrderings = contOrderBy.Orderings;
                        var contLambdaParam = BuildTypedLambdaParameter(null, rangeVar, context, contOrderings[0].Expression);
                        var contFirstKey = facade.Transform(contOrderings[0].Expression, context);
                        var contFirstDesc = contOrderings[0].AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                        var contComparator = new System.Text.StringBuilder($"Comparator.comparing({contLambdaParam} -> {contFirstKey}){contFirstDesc}");
                        for (int ci2 = 1; ci2 < contOrderings.Count; ci2++)
                        {
                            var cord = contOrderings[ci2];
                            var ck = facade.Transform(cord.Expression, context);
                            var cd = cord.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword) ? ".reversed()" : "";
                            contComparator.Append($"\n        .thenComparing({contLambdaParam} -> {ck}){cd}");
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
                    sb.Append("\n    .collect(Collectors.toCollection(() -> new ArrayList<>()))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    break;
                case GroupClauseSyntax contGroup:
                    var contGroupExpr = facade.Transform(contGroup.GroupExpression, context);
                    var contByExpr = facade.Transform(contGroup.ByExpression, context);
                    if (contGroupExpr != rangeVar)
                        sb.Append($"\n    .collect(Collectors.groupingBy({rangeVar} -> {contByExpr}," +
                                  $" Collectors.mapping({rangeVar} -> {contGroupExpr}, Collectors.toCollection(() -> new ArrayList<>()))))");
                    else
                        sb.Append($"\n    .collect(Collectors.groupingBy({rangeVar} -> {contByExpr}))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    break;
            }
        }
        }
        finally
        {
            // Always clear let-alias mappings so they don't leak into subsequent code
            context.QueryLetAliases.Clear();
        }

        return sb.ToString();
    }

    private static string BuildTypedLambdaParameter(ITypeSymbol? sourceType, string rangeVar, ConversionContext context, ExpressionSyntax? keyExpression)
    {
        var elementType = ExtractEnumerableElementType(sourceType);
        if (elementType == null && keyExpression != null && context.SemanticModel != null)
        {
            var rangeIdent = keyExpression
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(i => i.Identifier.Text == rangeVar);
            if (rangeIdent != null)
                elementType = context.SemanticModel.GetTypeInfo(rangeIdent).Type;
        }

        if (elementType == null)
            return rangeVar;

        var javaType = context.MapType(elementType);
        if (string.IsNullOrWhiteSpace(javaType) || javaType == elementType.ToDisplayString())
            return rangeVar;

        var boxed = ExpressionTransformerHelpers.BoxJavaPrimitiveType(javaType);
        return $"({boxed} {rangeVar})";
    }

    private static ITypeSymbol? ExtractEnumerableElementType(ITypeSymbol? sourceType)
    {
        if (sourceType is IArrayTypeSymbol arrayType)
            return arrayType.ElementType;

        if (sourceType is not INamedTypeSymbol named)
            return null;

        if (named.TypeArguments.Length == 1 && IsEnumerableNamedType(named))
            return named.TypeArguments[0];

        var ienum = named.AllInterfaces.FirstOrDefault(i => IsEnumerableNamedType(i) && i.TypeArguments.Length == 1);
        return ienum?.TypeArguments[0];
    }

    private static bool IsEnumerableNamedType(INamedTypeSymbol named)
        => named.Name == "IEnumerable" && named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";
}
