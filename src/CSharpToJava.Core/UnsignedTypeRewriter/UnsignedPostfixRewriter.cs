using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// Syntax-only second pass. The semantic pass annotates safe unsigned postfix
/// increments/decrements; this pass hoists value-used ones into a temp and
/// rewrites them as static UnsignedMath calls.
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
                .Where(UnsignedPostfixAnnotations.Has)
                .Where(p => !IsInsideNestedBlock(p, node) && !IsInsideLoopBody(p))
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
                var target = UnsignedPostfixAnnotations.GetTarget(postfix);
                var domain = target switch
                {
                    SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
                    SpecialType.System_UInt32 => UnsignedDomain.UInt32,
                    SpecialType.System_UInt64 => UnsignedDomain.UInt64,
                    _ => UnsignedDomain.Native,
                };
                if (domain == UnsignedDomain.Native)
                {
                    continue;
                }

                var method = postfix.IsKind(SyntaxKind.PostIncrementExpression) ? "Add" : "Subtract";
                var operand = postfix.Operand;
                var call = UnsignedMathSyntax.StaticCall(
                    method + UnsignedMathSyntax.Suffix(domain),
                    UnsignedMathSyntax.Normalize(operand, domain, target),
                    LiteralOne());
                var assignment = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    operand.WithoutTrivia(),
                    UnsignedMathSyntax.WrapTarget(target, call));

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

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
}
