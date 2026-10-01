// Cross-reference: do not confuse with Java/Rewriters/StringConcatRewriter.cs
// (Java-side transformer). This is the C# call-site preprocessor of C47;
// both files intentionally share the name but live in different namespaces.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EqualityConcat.Rules;

/// <summary>
/// Rewriter for string concatenation sites EC-3 (pure left-add chains folded
/// into a single <c>string.Concat(...)</c>, zero temporaries) and EC-4
/// (<c>+=</c> with temp lifting for unstable targets).
/// </summary>
internal static class StringConcatRewriter
{
    /// <summary>
    /// EC-3: folds the pure left-nested Add chain rooted at
    /// <paramref name="rootBinary"/> (already visited bottom-up) into one
    /// <c>string.Concat(o1, o2, ..., on)</c>. Right-associated adds stay
    /// nested (design §2 decision 6); mixed operands pass through unchanged.
    /// Every operand appears exactly once: no temporaries are introduced.
    /// </summary>
    public static (InvocationExpressionSyntax Invocation, int OperandCount) FoldLeftChain(
        BinaryExpressionSyntax rootBinary,
        SemanticModel model)
    {
        var operands = new List<ExpressionSyntax>();
        var current = rootBinary;

        while (true)
        {
            operands.Add(current.Right);
            var left = current.Left;

            // Only descend into left-nested adds that are *also* bound to
            // string.op_Addition (stops at user-defined operators etc.).
            if (left is BinaryExpressionSyntax leftBinary
                && leftBinary.OperatorToken.IsKind(SyntaxKind.PlusToken)
                && IsStringConcat(leftBinary, model))
            {
                current = leftBinary;
            }
            else
            {
                operands.Add(left);
                break;
            }
        }

        operands.Reverse();

        var arguments = SyntaxFactory.SeparatedList(
            operands.Select(SyntaxFactory.Argument));
        var invocation = SyntaxFactory.InvocationExpression(
            BuildConcatMemberAccess(),
            SyntaxFactory.ArgumentList(arguments));

        return (invocation, operands.Count);
    }

    /// <summary>Builds <c>target = string.Concat(target, value)</c> (fresh target instances).</summary>
    public static AssignmentExpressionSyntax RewriteCompound(
        Func<ExpressionSyntax> buildFreshTarget,
        ExpressionSyntax value)
    {
        var concatTarget = buildFreshTarget();
        var assignmentTarget = buildFreshTarget();

        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(concatTarget),
                SyntaxFactory.Argument(value),
            }));
        var concat = SyntaxFactory.InvocationExpression(
            BuildConcatMemberAccess(), argumentList);

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression, assignmentTarget, concat);
    }

    private static MemberAccessExpressionSyntax BuildConcatMemberAccess()
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.PredefinedType(
                SyntaxFactory.Token(SyntaxKind.StringKeyword)),
            SyntaxFactory.IdentifierName("Concat"));

    private static bool IsStringConcat(SyntaxNode node, SemanticModel model)
        => SemanticKindClassifier.Classify(node, model).Kind
            == EqualityConcatKind.StringConcat;
}

/// <summary>
/// EC-4 target analysis: decides which receiver/index subexpressions must be
/// hoisted into temps for single evaluation and produces fresh target
/// instances for the get and set positions (a syntax node can only occupy
/// one tree position, so each position is rebuilt).
/// <para>
/// Evaluation order (M0 probe, locked baseline):
/// receiver → index args → indexer get → value → set. Property: receiver →
/// get → value → set. Temps are registered in exactly that order.
/// </para>
/// </summary>
internal sealed class CompoundTargetRecipe
{
    private readonly List<(string Name, ExpressionSyntax Initializer)> _temps = new();
    private readonly EqualityConcatTempNameAllocator _allocator;

    private CompoundTargetRecipe(EqualityConcatTempNameAllocator allocator)
        => _allocator = allocator;

    public IReadOnlyList<(string Name, ExpressionSyntax Initializer)> Temps => _temps;

    /// <summary>
    /// Analyzes a (post-child-visit) assignment target. Returns null when the
    /// target shape cannot guarantee single evaluation (caller emits
    /// CS2J0475).
    /// </summary>
    public static (CompoundTargetRecipe? Recipe, Func<ExpressionSyntax>? BuildTarget) Analyze(
        ExpressionSyntax target,
        EqualityConcatTempNameAllocator allocator)
    {
        var recipe = new CompoundTargetRecipe(allocator);
        var buildTarget = recipe.AnalyzeTarget(target);
        if (buildTarget == null)
            return (null, null);
        return (recipe, buildTarget);
    }

    private Func<ExpressionSyntax>? AnalyzeTarget(ExpressionSyntax target)
        => target switch
        {
            IdentifierNameSyntax identifier => () => RebuildIdentifier(identifier),
            MemberAccessExpressionSyntax member => AnalyzeMember(member),
            ElementAccessExpressionSyntax element => AnalyzeElement(element),
            _ => null,
        };

    private Func<ExpressionSyntax>? AnalyzeMember(MemberAccessExpressionSyntax member)
    {
        if (member.Expression is ConditionalAccessExpressionSyntax)
            return null;

        if (IsStable(member.Expression))
        {
            return () => SyntaxFactory.MemberAccessExpression(
                member.Kind(),
                DuplicateStable(member.Expression),
                SyntaxFactory.IdentifierName(member.Name.Identifier));
        }

        var tempName = Hoist(member.Expression);
        return () => SyntaxFactory.MemberAccessExpression(
            member.Kind(),
            SyntaxFactory.IdentifierName(tempName),
            SyntaxFactory.IdentifierName(member.Name.Identifier));
    }

    private Func<ExpressionSyntax>? AnalyzeElement(ElementAccessExpressionSyntax element)
    {
        Func<ExpressionSyntax> receiverBuilder;
        if (element.Expression is ConditionalAccessExpressionSyntax)
            return null;
        if (IsStable(element.Expression))
        {
            var stableReceiver = element.Expression;
            receiverBuilder = () => DuplicateStable(stableReceiver);
        }
        else
        {
            var tempName = Hoist(element.Expression);
            receiverBuilder = () => SyntaxFactory.IdentifierName(tempName);
        }

        var argumentBuilders = new List<Func<ArgumentSyntax>>();
        foreach (var argument in element.ArgumentList.Arguments)
        {
            var expression = argument.Expression;
            if (IsStable(expression))
            {
                argumentBuilders.Add(() => SyntaxFactory.Argument(
                    DuplicateStable(expression)));
            }
            else
            {
                var tempName = Hoist(expression);
                argumentBuilders.Add(() => SyntaxFactory.Argument(
                    SyntaxFactory.IdentifierName(tempName)));
            }
        }

        return () =>
        {
            var arguments = SyntaxFactory.SeparatedList(
                argumentBuilders.Select(b => b()));
            return SyntaxFactory.ElementAccessExpression(
                receiverBuilder(),
                SyntaxFactory.BracketedArgumentList(arguments));
        };
    }

    /// <summary>
    /// A subexpression is stable when repeated evaluation is side-effect-free
    /// and shape-preserving: names, <c>this</c>, literals, and chains thereof.
    /// Anything else (calls, indexers, conditional access, binary expressions,
    /// casts, ...) is conservatively hoisted.
    /// </summary>
    private static bool IsStable(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
            LiteralExpressionSyntax => true,
            ParenthesizedExpressionSyntax p => IsStable(p.Expression),
            MemberAccessExpressionSyntax m => IsStable(m.Expression),
            _ => false,
        };

    /// <summary>
    /// Rebuilds a stable expression as a fresh node instance (identifiers,
    /// <c>this</c>, literals, and simple member chains only).
    /// </summary>
    private static ExpressionSyntax DuplicateStable(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax id => RebuildIdentifier(id),
            ThisExpressionSyntax => SyntaxFactory.ThisExpression(),
            LiteralExpressionSyntax literal => SyntaxFactory.LiteralExpression(
                literal.Kind(), DuplicateLiteralToken(literal.Token)),
            ParenthesizedExpressionSyntax p => SyntaxFactory
                .ParenthesizedExpression(DuplicateStable(p.Expression)),
            MemberAccessExpressionSyntax m => SyntaxFactory.MemberAccessExpression(
                m.Kind(),
                DuplicateStable(m.Expression),
                SyntaxFactory.IdentifierName(m.Name.Identifier)),
            _ => throw new InvalidOperationException(
                $"Unstable expression reached DuplicateStable: {expression.Kind()}"),
        };

    private static IdentifierNameSyntax RebuildIdentifier(IdentifierNameSyntax identifier)
        => SyntaxFactory.IdentifierName(
            SyntaxFactory.Identifier(identifier.Identifier.Text));

    private string Hoist(ExpressionSyntax expression)
    {
        var name = _allocator.Allocate();
        _temps.Add((name, expression));
        return name;
    }

    /// <summary>
    /// Rebuilds a literal token preserving its source text (numeric suffixes,
    /// string/char contents); ParseToken round-trips every literal shape.
    /// Covers keyword literals <c>true</c>/<c>false</c>/<c>null</c>/<c>default</c>.
    /// </summary>
    private static SyntaxToken DuplicateLiteralToken(SyntaxToken token)
    {
        switch (token.Kind())
        {
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.DefaultKeyword:
                return SyntaxFactory.Token(token.Kind());
            default:
                return SyntaxFactory.ParseToken(token.Text);
        }
    }
}
