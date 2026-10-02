using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// IE-CONCAT / IE-FOLD emission (design §2.2, §4.4 IE-2/IE-3).
///
/// Guarantees:
/// <list type="bullet">
/// <item>hole expressions are reused ZERO-COPY from the visited tree (each
/// appears exactly once — evaluation order/count identical to the default
/// handler); no temporaries are created (C23 main path PendingTemps empty);</item>
/// <item>text segments emit regular string literals via SyntaxFactory
/// (escaping handled by the factory), empty text segments are dropped;</item>
/// <item>uint/ulong holes -> x.ToString(); byte/ushort -> ((uint)x).ToString();
/// proven-safe ref struct holes -> x.ToString(); null holes pass through
/// unwrapped (no ?? "") and value types rely on natural boxing;</item>
/// <item>constant sites -> one bare literal, no Concat/Format wrapper.</item>
/// </list>
/// </summary>
internal static class ConcatEmitter
{
    /// <summary>IE-FOLD: a bare regular string literal carrying the compiler constant.</summary>
    public static ExpressionSyntax EmitFold(string? constantValue)
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(constantValue ?? string.Empty));

    /// <summary>
    /// IE-CONCAT for one visited site. <paramref name="visited"/> already has
    /// its inner interpolations rewritten (bottom-up); <paramref name="plan"/>
    /// segments are in the same content order as the original site.
    /// </summary>
    public static ExpressionSyntax EmitConcat(
        InterpolatedStringExpressionSyntax visited,
        InterpolationPlan plan)
    {
        var arguments = new List<ExpressionSyntax>();
        var holeIndex = 0;

        foreach (var content in visited.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                {
                    var value = InterpolationSyntaxFacts.GetTextValue(text);
                    if (value.Length > 0)
                        arguments.Add(MakeLiteral(value));
                    break;
                }

                case InterpolationSyntax hole:
                {
                    var info = plan.Segments
                        .OfType<InterpolationSegment.Hole>()
                        .ElementAt(holeIndex)
                        .Info;
                    holeIndex++;
                    arguments.Add(WrapHole(hole.Expression, info));
                    break;
                }
            }
        }

        // Defensive: a text-only site normally folds above. A zero-argument
        // Concat is invalid, and one text argument is just the literal itself.
        if (arguments.Count == 0)
            return MakeLiteral(string.Empty);
        if (arguments.Count == 1 && visited.Contents
                .All(c => c is InterpolatedStringTextSyntax))
            return arguments[0];

        return MakeStringConcat(arguments);
    }

    private static ExpressionSyntax WrapHole(ExpressionSyntax expression, HoleInfo info)
    {
        switch (info.UnsignedKind)
        {
            case UnsignedHoleKind.UInt32:
            case UnsignedHoleKind.UInt64:
                // x.ToString() — the downstream unsigned pass recognizes this
                // exact shape and routes it to its unsigned helper.
                return InvokeParameterlessToString(expression);

            case UnsignedHoleKind.Byte:
            case UnsignedHoleKind.UInt16:
                // ((uint)x).ToString(): one CastExpression plus ToString,
                // matching the uint interception contract.
                var cast = SyntaxFactory.CastExpression(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.UIntKeyword)),
                    expression);
                return InvokeParameterlessToString(cast);
        }

        if (info.HasSafeRefStructToString)
            return InvokeParameterlessToString(expression);

        // Primitive/enum/string/object holes pass through unwrapped: null
        // stays null (Concat yields "") and value types box naturally.
        return expression;
    }

    private static InvocationExpressionSyntax InvokeParameterlessToString(ExpressionSyntax receiver)
    {
        var safeReceiver = MemberAccessNeedsParentheses(receiver)
            ? SyntaxFactory.ParenthesizedExpression(receiver)
            : receiver;

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                safeReceiver,
                SyntaxFactory.IdentifierName("ToString")),
            SyntaxFactory.ArgumentList());
    }

    /// <summary>
    /// True when appending <c>.Member</c> directly would change the parse
    /// (binary/conditional/assignment/...). Tight-binding primary expressions
    /// stay unparenthesized.
    /// </summary>
    private static bool MemberAccessNeedsParentheses(ExpressionSyntax expression)
        => expression switch
        {
            // Primary expressions bind tightly enough for .Member.
            LiteralExpressionSyntax => false,
            IdentifierNameSyntax => false,
            QualifiedNameSyntax => false,
            GenericNameSyntax => false,
            MemberAccessExpressionSyntax => false,
            MemberBindingExpressionSyntax => false,
            InvocationExpressionSyntax => false,
            ElementAccessExpressionSyntax => false,
            ObjectCreationExpressionSyntax => false,
            ImplicitObjectCreationExpressionSyntax => false,
            ArrayCreationExpressionSyntax => false,
            ImplicitArrayCreationExpressionSyntax => false,
            StackAllocArrayCreationExpressionSyntax => false,
            ThisExpressionSyntax => false,
            BaseExpressionSyntax => false,
            ParenthesizedExpressionSyntax => false,
            // '(uint)b' binds looser than member access: '(uint)b.ToString()'
            // would invoke ToString on b then cast the string — wrap as
            // '((uint)b).ToString()'.
            PostfixUnaryExpressionSyntax => false,
            ConditionalAccessExpressionSyntax => false,
            InterpolatedStringExpressionSyntax => false,
            InitializerExpressionSyntax => false,
            _ => true,
        };

    private static LiteralExpressionSyntax MakeLiteral(string value)
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(value));

    private static InvocationExpressionSyntax MakeStringConcat(IReadOnlyList<ExpressionSyntax> arguments)
    {
        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                arguments.Select(static a => SyntaxFactory.Argument(a))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                SyntaxFactory.IdentifierName("Concat")),
            argumentList);
    }
}
