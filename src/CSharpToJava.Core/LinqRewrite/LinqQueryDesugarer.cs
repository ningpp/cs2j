using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpToJava.Core.LinqRewrite;

/// <summary>
/// A pure-syntactic rewriter that desugars LINQ query expressions into
/// equivalent method-call chains (Where / Select / OrderBy / GroupBy, etc.).
/// No semantic model is required.
/// </summary>
public sealed class LinqQueryDesugarer : CSharpSyntaxRewriter
{
    public int DesugaredCount { get; private set; }

    public override SyntaxNode? VisitQueryExpression(QueryExpressionSyntax node)
    {
        try
        {
            var result = TryDesugar(node);
            if (result != null)
            {
                DesugaredCount++;
                return result.WithTriviaFrom(node);
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidOperationException or ArgumentException)
        {
            // Syntactic desugaring failed for this query expression.
            // Fall back to base visitor; QueryExpressionTransformer handles it during Java emit.
        }

        return base.VisitQueryExpression(node);
    }

    private static ExpressionSyntax? TryDesugar(QueryExpressionSyntax query)
    {
        // Query continuations (into g ...) require materialising an intermediate
        // result and re-querying it; too complex for pure syntactic desugaring.
        if (query.Body.Continuation != null)
            return null;

        var clauses = query.Body.Clauses.ToList();

        // Fall back for complex clauses that require transparent identifiers or
        // GroupJoin semantics which need semantic analysis to desugar correctly.
        if (clauses.Any(c => c is LetClauseSyntax || c is JoinClauseSyntax || c is FromClauseSyntax))
            return null;

        var rangeVar = query.FromClause.Identifier;
        ExpressionSyntax current = query.FromClause.Expression;

        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case WhereClauseSyntax where:
                    current = MakeCall(current, "Where", MakeLambda(rangeVar, where.Condition));
                    break;

                case OrderByClauseSyntax orderBy:
                    var orderings = orderBy.Orderings;
                    bool firstKey = true;
                    foreach (var ordering in orderings)
                    {
                        bool descending = ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword);
                        string method = firstKey
                            ? (descending ? "OrderByDescending" : "OrderBy")
                            : (descending ? "ThenByDescending" : "ThenBy");
                        current = MakeCall(current, method, MakeLambda(rangeVar, ordering.Expression));
                        firstKey = false;
                    }
                    break;

                default:
                    return null;
            }
        }

        switch (query.Body.SelectOrGroup)
        {
            case SelectClauseSyntax select:
                // Identity select (from x in xs select x) — skip the .Select() call.
                if (!(select.Expression is IdentifierNameSyntax id && id.Identifier.Text == rangeVar.Text))
                    current = MakeCall(current, "Select", MakeLambda(rangeVar, select.Expression));
                break;

            case GroupClauseSyntax group:
                var keySelector = MakeLambda(rangeVar, group.ByExpression);
                if (group.GroupExpression is IdentifierNameSyntax gId && gId.Identifier.Text == rangeVar.Text)
                    current = MakeCall(current, "GroupBy", keySelector);
                else
                    current = MakeCall(current, "GroupBy", keySelector, MakeLambda(rangeVar, group.GroupExpression));
                break;

            default:
                return null;
        }

        return current;
    }

    private static SimpleLambdaExpressionSyntax MakeLambda(SyntaxToken param, ExpressionSyntax body)
        => SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(param), body);

    private static InvocationExpressionSyntax MakeCall(
        ExpressionSyntax receiver,
        string method,
        params ExpressionSyntax[] args)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName(method)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args.Select(SyntaxFactory.Argument))));
}
