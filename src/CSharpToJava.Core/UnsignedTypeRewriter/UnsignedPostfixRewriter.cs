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
                .OrderBy(p => p.SpanStart)
                .ToList();

            // 只有无条件执行的语句种类才允许把自增拆成临时变量 + 赋值;
            // if/while/switch 等条件语句里的 i++ 一旦提前拆出,条件不满足时
            // 也会执行自增,直接改变语义(保持原生 i++,Java 侧逐位一致)。
            if (postfixes.Count == 0 || !IsUnconditionallyExecutedStatement(statement))
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

                var isIncrement = postfix.IsKind(SyntaxKind.PostIncrementExpression);
                var operand = postfix.Operand.WithoutTrivia();
                // 语句开头的前导 trivia(含 #if/#endif)必须跟着首个语句走,
                // 否则条件编译指令会丢失或失衡。
                var statementLeading = postfix.GetLeadingTrivia();

                ExpressionSyntax value = domain == UnsignedDomain.Int32
                    ? SyntaxFactory.BinaryExpression(
                        isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                        operand,
                        LiteralOne())
                    : UnsignedMathSyntax.StaticCall(
                        (isIncrement ? "Add" : "Subtract") + UnsignedMathSyntax.Suffix(domain),
                        UnsignedMathSyntax.Normalize(operand, domain, target),
                        LiteralOne());
                var assignment = UnsignedRewriteAnnotations.Mark(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        operand,
                        UnsignedMathSyntax.WrapTarget(target, value)));

                if (IsStandaloneExpressionStatement(postfix))
                {
                    replacements[postfix] = WithLeading(assignment, statementLeading);
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
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(operand)))));

                    var indent = statementLeading.LastOrDefault(
                        t => t.IsKind(SyntaxKind.WhitespaceTrivia));
                    var assignmentLeading = SyntaxFactory.TriviaList(
                        SyntaxFactory.CarriageReturnLineFeed);
                    if (!indent.IsKind(SyntaxKind.None))
                    {
                        assignmentLeading = assignmentLeading.Add(indent);
                    }

                    newStatements.Add(WithLeading(
                        UnsignedRewriteAnnotations.Mark(tempDeclaration), statementLeading));
                    newStatements.Add(SyntaxFactory.ExpressionStatement(assignment)
                        .WithLeadingTrivia(assignmentLeading)
                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }

                UsedExtensions = true;
                _stats.OperatorsRewritten++;
            }

            current = current.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original]);
            newStatements.Add(UnsignedRewriteAnnotations.Mark(current));
        }

        return changed
            ? node.WithStatements(SyntaxFactory.List(newStatements))
            : node;
    }

    private static StatementSyntax WithLeading(StatementSyntax statement, SyntaxTriviaList leading)
        => leading.Count == 0 ? statement : statement.WithLeadingTrivia(leading);

    private static ExpressionSyntax WithLeading(ExpressionSyntax expression, SyntaxTriviaList leading)
        => leading.Count == 0 ? expression : expression.WithLeadingTrivia(leading);

    /// <summary>语句到达即无条件执行(其内表达式的求值不被任何条件跳过)。</summary>
    private static bool IsUnconditionallyExecutedStatement(StatementSyntax statement) => statement switch
    {
        ExpressionStatementSyntax => true,
        LocalDeclarationStatementSyntax => true,
        ReturnStatementSyntax => true,
        ThrowStatementSyntax => true,
        YieldStatementSyntax => true,
        _ => false,
    };

    private static bool IsStandaloneExpressionStatement(PostfixUnaryExpressionSyntax node)
    {
        var parent = node.Parent;
        return parent is ExpressionStatementSyntax expressionStatement
               && expressionStatement.Expression == node;
    }

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
}
