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
        var outerSource = query.FromClause.Expression;

        // When the outer from-clause carries an explicit type annotation
        // (e.g. `from Derived a in bases`), C# semantics are equivalent to
        // `bases.Cast<Derived>()`.  We represent that as a Select-cast so the
        // rebuilt semantic model gives the lambda parameter the correct type:
        //   bases.Select(a => (Derived)a) ...
        // Without this, the rebuilt model types `a` as the collection element
        // type (e.g. Base), causing downstream anonymous-record fields to carry
        // the wrong type and failing Java compilation.
        if (!IsVarOrImplicit(query.FromClause.Type))
        {
            outerSource = MakeCall(outerSource, "Select",
                MakeLambda(query.FromClause.Identifier,
                    SyntaxFactory.CastExpression(
                        query.FromClause.Type,
                        SyntaxFactory.IdentifierName(query.FromClause.Identifier))));
        }

        return ProcessQueryBody(outerSource, query.FromClause.Identifier, query.Body);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="type"/> is absent
    /// or is the implicitly-typed <c>var</c> keyword.
    /// </summary>
    private static bool IsVarOrImplicit(TypeSyntax? type) =>
        type is null ||
        (type is IdentifierNameSyntax id && id.Identifier.Text == "var");

    private static ExpressionSyntax? ProcessQueryBody(
        ExpressionSyntax source,
        SyntaxToken rangeVar,
        QueryBodySyntax body)
    {
        var result = DesugarBodyClauses(
            source, rangeVar,
            body.Clauses.ToList(), 0,
            body.SelectOrGroup,
            new Dictionary<string, ExpressionSyntax>());

        if (result == null) return null;

        // Handle query continuation (into g <body>)
        if (body.Continuation != null)
        {
            return ProcessQueryBody(
                result,
                body.Continuation.Identifier,
                body.Continuation.Body);
        }

        return result;
    }

    private static ExpressionSyntax? DesugarBodyClauses(
        ExpressionSyntax source,
        SyntaxToken rangeVar,
        List<QueryClauseSyntax> clauses,
        int startIndex,
        SelectOrGroupClauseSyntax selectOrGroup,
        Dictionary<string, ExpressionSyntax> letBindings)
    {
        for (int i = startIndex; i < clauses.Count; i++)
        {
            switch (clauses[i])
            {
                case WhereClauseSyntax where:
                    source = MakeCall(source, "Where",
                        MakeLambda(rangeVar, Substitute(where.Condition, letBindings)));
                    break;

                case OrderByClauseSyntax orderBy:
                    bool firstKey = true;
                    foreach (var ordering in orderBy.Orderings)
                    {
                        bool descending = ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword);
                        string method = firstKey
                            ? (descending ? "OrderByDescending" : "OrderBy")
                            : (descending ? "ThenByDescending" : "ThenBy");
                        source = MakeCall(source, method,
                            MakeLambda(rangeVar, Substitute(ordering.Expression, letBindings)));
                        firstKey = false;
                    }
                    break;

                case LetClauseSyntax let:
                    // Inline substitution: register let variable → expression.
                    // No method call emitted; subsequent clauses will have the
                    // let expression inlined wherever the let variable is referenced.
                    letBindings = new Dictionary<string, ExpressionSyntax>(letBindings)
                    {
                        [let.Identifier.Text] = Substitute(let.Expression, letBindings)
                    };
                    break;

                case FromClauseSyntax from:
                    // Push all remaining clauses + select into a nested inner chain
                    // wrapped inside .SelectMany(outerVar => innerChain).
                    // Outer variables are captured by closure in the inner lambdas.
                    var innerSource = Substitute(from.Expression, letBindings);

                    // If the inner from-clause has an explicit type annotation,
                    // insert a cast-select so the element type is preserved:
                    //   from Derived b in others  →  others.Select(b => (Derived)b)
                    if (!IsVarOrImplicit(from.Type))
                    {
                        innerSource = MakeCall(innerSource, "Select",
                            MakeLambda(from.Identifier,
                                SyntaxFactory.CastExpression(
                                    from.Type,
                                    SyntaxFactory.IdentifierName(from.Identifier))));
                    }

                    var innerResult = DesugarBodyClauses(
                        innerSource, from.Identifier,
                        clauses, i + 1, selectOrGroup,
                        new Dictionary<string, ExpressionSyntax>(letBindings));
                    if (innerResult == null) return null;
                    source = MakeCall(source, "SelectMany", MakeLambda(rangeVar, innerResult));
                    return source; // All remaining clauses consumed by inner chain

                case JoinClauseSyntax join when join.Into == null:
                    // join y in inner on outerKey equals innerKey
                    // Only desugar when join is the last clause (no transparent identifiers needed).
                    if (i < clauses.Count - 1) return null;

                    if (selectOrGroup is not SelectClauseSyntax joinSel) return null;
                    source = MakeCall(source, "Join",
                        Substitute(join.InExpression, letBindings),
                        MakeLambda(rangeVar, Substitute(join.LeftExpression, letBindings)),
                        MakeLambda(join.Identifier, join.RightExpression),
                        MakeParenLambda(rangeVar, join.Identifier, Substitute(joinSel.Expression, letBindings)));
                    return source; // selectOrGroup consumed

                case JoinClauseSyntax joinInto when joinInto.Into != null:
                    // join y in inner on outerKey equals innerKey into g
                    // Only desugar when join-into is the last clause.
                    if (i < clauses.Count - 1) return null;

                    if (selectOrGroup is not SelectClauseSyntax gjSel) return null;
                    source = MakeCall(source, "GroupJoin",
                        Substitute(joinInto.InExpression, letBindings),
                        MakeLambda(rangeVar, Substitute(joinInto.LeftExpression, letBindings)),
                        MakeLambda(joinInto.Identifier, joinInto.RightExpression),
                        MakeParenLambda(rangeVar, joinInto.Into.Identifier, Substitute(gjSel.Expression, letBindings)));
                    return source; // selectOrGroup consumed

                default:
                    return null;
            }
        }

        // Process final select/group clause
        switch (selectOrGroup)
        {
            case SelectClauseSyntax select:
                var selBody = Substitute(select.Expression, letBindings);
                // Identity select (from x in xs select x) — skip the .Select() call.
                if (!(selBody is IdentifierNameSyntax id && id.Identifier.Text == rangeVar.Text))
                    source = MakeCall(source, "Select", MakeLambda(rangeVar, selBody));
                break;

            case GroupClauseSyntax group:
                var keyBody = Substitute(group.ByExpression, letBindings);
                var grpBody = Substitute(group.GroupExpression, letBindings);
                if (grpBody is IdentifierNameSyntax gId && gId.Identifier.Text == rangeVar.Text)
                    source = MakeCall(source, "GroupBy", MakeLambda(rangeVar, keyBody));
                else
                    source = MakeCall(source, "GroupBy", MakeLambda(rangeVar, keyBody), MakeLambda(rangeVar, grpBody));
                break;

            default:
                return null;
        }

        return source;
    }

    // ─── Inline substitution for let-bindings ───

    private static ExpressionSyntax Substitute(ExpressionSyntax expr, Dictionary<string, ExpressionSyntax> bindings)
    {
        if (bindings.Count == 0) return expr;
        return (ExpressionSyntax)new IdentifierSubstituter(bindings).Visit(expr);
    }

    private sealed class IdentifierSubstituter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, ExpressionSyntax> _bindings;
        public IdentifierSubstituter(Dictionary<string, ExpressionSyntax> bindings) => _bindings = bindings;

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (_bindings.TryGetValue(node.Identifier.Text, out var replacement))
                return SyntaxFactory.ParenthesizedExpression(replacement).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }

    private static SimpleLambdaExpressionSyntax MakeLambda(SyntaxToken param, ExpressionSyntax body)
        => SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(param), body);

    private static ParenthesizedLambdaExpressionSyntax MakeParenLambda(
        SyntaxToken param1, SyntaxToken param2, ExpressionSyntax body)
        => SyntaxFactory.ParenthesizedLambdaExpression(
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Parameter(param1),
                SyntaxFactory.Parameter(param2)
            })),
            body);

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
