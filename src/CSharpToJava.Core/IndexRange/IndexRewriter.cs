using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IndexRange;

/// <summary>
/// IR-1: rewrites <c>x[^n]</c> element access into an explicit int index
/// <c>x[x.Length - n]</c> (or the bound Length/Count member). Non-pure
/// receiver/operand are lifted to single-evaluation temps, but only the
/// temps actually required are emitted.
/// </summary>
internal static class IndexRewriter
{
    internal static ElementAccessExpressionSyntax RewriteHatElement(
        ElementAccessExpressionSyntax visited,
        PrefixUnaryExpressionSyntax hat,
        ReceiverInfo info,
        bool lift,
        SyntaxNode anchor,
        TempNameAllocator names,
        List<TempLifter.PendingTemp> pending,
        IndexRangeStatistics stats)
    {
        stats.HatElementSites++;

        var receiverPure = IndexRangeSyntaxFacts.IsPure(visited.Expression);
        var operandPure = IndexRangeSyntaxFacts.IsPure(hat.Operand);

        string? rName = null;
        string? nName = null;
        string? lName = null;

        if (lift)
        {
            var order = 0;

            if (!receiverPure)
            {
                rName = names.Allocate("r");
                pending.Add(new TempLifter.PendingTemp(
                    TempLifter.DeclareVar(rName, visited.Expression),
                    anchor, order++));
                stats.ReceiverTempsLifted++;
            }

            if (!operandPure)
            {
                nName = names.Allocate("n");
                pending.Add(new TempLifter.PendingTemp(
                    TempLifter.DeclareVar(nName, hat.Operand),
                    anchor, order++));
                stats.BoundTempsLifted++;
            }

            // Length is read on the (temp-bound) receiver; a dedicated temp
            // keeps the read single and ordered after the operand eval.
            if (rName != null)
            {
                lName = names.Allocate("l");
                pending.Add(new TempLifter.PendingTemp(
                    TempLifter.DeclareInt(
                        lName,
                        SimpleMemberAccess(IdentifierName(rName), info.LengthMemberName)),
                    anchor, order));
                stats.LengthTempReads++;
            }
        }

        var effectiveReceiver = rName != null
            ? IdentifierName(rName)
            : visited.Expression;

        var lengthExpression = lName != null
            ? (ExpressionSyntax)IdentifierName(lName)
            : SimpleMemberAccess(effectiveReceiver, info.LengthMemberName);

        var operandExpression = nName != null
            ? IdentifierName(nName)
            : hat.Operand;

        var index = Subtract(lengthExpression, operandExpression);

        var rewritten = visited.WithExpression(effectiveReceiver);
        return WithIndexArgument(rewritten, index);
    }

    private static ElementAccessExpressionSyntax WithIndexArgument(
        ElementAccessExpressionSyntax elementAccess,
        ExpressionSyntax index)
    {
        var newArgument = elementAccess.ArgumentList.Arguments[0]
            .WithExpression(index);
        return elementAccess.WithArgumentList(
            elementAccess.ArgumentList.WithArguments(
                SingletonSeparatedList(newArgument)));
    }

    private static ExpressionSyntax Subtract(
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var folded = IndexRangeSyntaxFacts.TryFoldSubtract(left, right);
        if (folded != null)
        {
            return folded;
        }

        // 右操作数为复合算术表达式时加括号：len - (i + 1) 不能发射成
        // len - i + 1（hat 索引 a[^(i+1)] 的语义是 len-(i+1)）。
        if (right is BinaryExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            right = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParenthesizedExpression(right);
        }

        return BinaryExpression(SyntaxKind.SubtractExpression, left, right);
    }

    private static MemberAccessExpressionSyntax SimpleMemberAccess(
        ExpressionSyntax expression, string memberName)
        => MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression,
            IdentifierName(memberName));
}
