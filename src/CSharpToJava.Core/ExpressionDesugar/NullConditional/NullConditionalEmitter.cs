using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Pure rendering for C35 (design §4.3): placeholder-binding substitution,
/// null-check ternaries (incl. the <c>(T?)</c> preserving cast), segment
/// declarations/guarded prefetches and nested if blocks. Every helper is
/// functional: same inputs give equivalent output, no state.
/// </summary>
internal static class NullConditionalEmitter
{
    // ------------------------------------------------------------------
    // Placeholder binding substitution.
    // ------------------------------------------------------------------

    /// <summary>
    /// Replaces the terminal/segment expression's leftmost placeholder
    /// binding with accesses on <paramref name="receiver"/>: a
    /// <see cref="MemberBindingExpressionSyntax"/> becomes
    /// <c>receiver.Name</c>; an <see cref="ElementBindingExpressionSyntax"/>
    /// becomes <c>receiver[args]</c>.
    /// </summary>
    public static ExpressionSyntax ReplaceRootBinding(
        ExpressionSyntax bindingRootedExpression,
        ExpressionSyntax receiver)
    {
        var binding = NullConditionalSyntaxFacts.FindRootBinding(
            bindingRootedExpression);
        if (binding == null)
            return bindingRootedExpression;

        var replacement = binding switch
        {
            MemberBindingExpressionSyntax memberBinding
                => (ExpressionSyntax)SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    memberBinding.Name),
            ElementBindingExpressionSyntax elementBinding
                => SyntaxFactory.ElementAccessExpression(
                    receiver,
                    elementBinding.ArgumentList),
            _ => receiver,
        };

        return (ExpressionSyntax)new BindingNodeSubstitutor(binding, replacement)
            .Visit(bindingRootedExpression)!;
    }

    /// <summary>Replaces exactly one node (the leftmost binding), then passes through.</summary>
    private sealed class BindingNodeSubstitutor : CSharpSyntaxRewriter
    {
        private readonly SyntaxNode _target;
        private readonly SyntaxNode _replacement;
        private bool _done;

        public BindingNodeSubstitutor(SyntaxNode target, SyntaxNode replacement)
        {
            _target = target;
            _replacement = replacement;
        }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (!_done && node == _target)
            {
                _done = true;
                return _replacement;
            }

            return base.Visit(node);
        }
    }

    // ------------------------------------------------------------------
    // Value consumption (single segment: Inline / TempTernary).
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the single-segment value consumption:
    /// <c>receiver != null ? terminal-on-receiver : null</c>, with the
    /// <c>(T?)</c> cast when the terminal lifts a value type.
    /// </summary>
    /// <param name="terminal">Visited terminal expression (binding-rooted).</param>
    /// <param name="receiverInCondition">Expression compared against null and used as the binding receiver.</param>
    public static ExpressionSyntax BuildSingleSegmentTernary(
        ExpressionSyntax terminal,
        ExpressionSyntax receiverInCondition,
        ChainPlan plan)
    {
        var terminalAccess = ReplaceRootBinding(terminal, receiverInCondition);

        ExpressionSyntax whenTrue = plan.CastToNullable
            ? SyntaxFactory.CastExpression(plan.NullableCastType!, terminalAccess)
            : terminalAccess;
        ExpressionSyntax whenFalse = BuildNullBranch(plan);

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            receiverInCondition,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

        return SyntaxFactory.ConditionalExpression(
            condition, whenTrue, whenFalse);
    }

    /// <summary>The null literal (with the <c>(T?)</c> cast when lifting).</summary>
    public static ExpressionSyntax BuildNullBranch(ChainPlan plan)
    {
        ExpressionSyntax nullLiteral =
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return plan.CastToNullable
            ? SyntaxFactory.CastExpression(plan.NullableCastType!, nullLiteral)
            : nullLiteral;
    }
}
