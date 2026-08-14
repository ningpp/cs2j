using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Syntax-only second pass. The semantic pass annotates safe unsigned postfix
/// increments/decrements; this pass hoists value-used ones into a temp
/// (`var t = x; x = x.Add(1);`) and rewrites standalone statement ones into
/// assignments, preserving `a++`'s old-value semantics.
/// </summary>
internal sealed class UnsignedPostfixRewriter : CSharpSyntaxRewriter
{
    private readonly UnsignedTypeRewriterStatistics _stats;
    private int _tempCounter;

    public bool UsedExtensions { get; private set; }

    public UnsignedPostfixRewriter(UnsignedTypeRewriterStatistics stats)
    {
        _stats = stats;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        var changed = false;

        foreach (var statement in node.Statements)
        {
            var postfixes = statement.DescendantNodes()
                .OfType<PostfixUnaryExpressionSyntax>()
                .Where(p => p.HasAnnotation(UnsignedPostfixAnnotations.Postfix)
                            && !IsInsideNestedBlock(p, node)
                            && !IsInsideLoopBody(p))
                .OrderBy(p => p.SpanStart)
                .ToList();

            if (postfixes.Count == 0)
            {
                newStatements.Add(statement);
                continue;
            }

            changed = true;
            var current = statement;
            var replacements = new Dictionary<PostfixUnaryExpressionSyntax, ExpressionSyntax>();
            foreach (var postfix in postfixes)
            {
                var method = postfix.IsKind(SyntaxKind.PostIncrementExpression) ? "Add" : "Subtract";
                var operand = postfix.Operand;
                var assignment = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    operand.WithoutTrivia(),
                    Invoke(Clone(operand), method, LiteralOne()));

                if (IsStandaloneExpressionStatement(postfix))
                {
                    replacements[postfix] = assignment;
                }
                else
                {
                    var tempName = $"__cs2j_unsigned_{_tempCounter++}";
                    var tempIdentifier = SyntaxFactory.IdentifierName(tempName);
                    replacements[postfix] = tempIdentifier.WithTriviaFrom(postfix);

                    var tempDeclaration = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(operand.WithoutTrivia())))));

                    newStatements.Add(tempDeclaration);
                    newStatements.Add(SyntaxFactory.ExpressionStatement(assignment));
                }

                UsedExtensions = true;
                _stats.OperatorsRewritten++;
            }

            current = current.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original]);
            newStatements.Add(current);
        }

        return changed
            ? node.WithStatements(SyntaxFactory.List(newStatements))
            : node;
    }

    private static bool IsStandaloneExpressionStatement(PostfixUnaryExpressionSyntax node)
    {
        var parent = node.Parent;
        return parent is ExpressionStatementSyntax expressionStatement
               && expressionStatement.Expression == node;
    }

    private static bool IsInsideNestedBlock(SyntaxNode node, BlockSyntax block)
    {
        for (SyntaxNode? current = node.Parent; current != null && current != block; current = current.Parent)
        {
            if (current is BlockSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideLoopBody(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is WhileStatementSyntax whileStatement
                && whileStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is DoStatementSyntax doStatement
                && doStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is ForStatementSyntax forStatement
                && forStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }

            if (current is ForEachStatementSyntax forEachStatement
                && forEachStatement.Statement.Span.Contains(node.Span))
            {
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax Clone(ExpressionSyntax expression)
        => SyntaxFactory.ParseExpression(expression.ToString());

    private static InvocationExpressionSyntax Invoke(
        ExpressionSyntax receiver, string method, ExpressionSyntax? argument = null)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            EnsureReceiver(receiver),
            SyntaxFactory.IdentifierName(method));

        var argumentList = argument == null
            ? SyntaxFactory.ArgumentList()
            : SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(argument) }));

        return SyntaxFactory.InvocationExpression(memberAccess, argumentList);
    }

    private static ExpressionSyntax EnsureReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or ParenthesizedExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or LiteralExpressionSyntax => receiver,
        _ => SyntaxFactory.ParenthesizedExpression(receiver),
    };

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
}
