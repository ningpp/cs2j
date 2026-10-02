using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// IE-FORMAT emission (design §2.2 composite row + R12, §4.4 IE-4): one
/// <c>string.Format(compositeLiteral, h0, h1, …)</c> per site.
///
/// Guarantees:
/// <list type="bullet">
/// <item>the composite is built from decoded text (ValueText) with literal
/// braces doubled (<c>{</c>→<c>{{</c>, <c>}</c>→<c>}}</c>; <c>%</c> is never
/// touched) and hole placeholders <c>{i}</c>/<c>{i,align}</c>/<c>{i:fmt}</c>/
/// <c>{i,align:fmt}</c> — i is the hole ordinal only, in text order;</item>
/// <item>the composite is emitted as ONE regular C# string literal via
/// SyntaxFactory.Literal (quotes/newlines/backslashes are escaped by the
/// factory, never by hand);</item>
/// <item>hole expressions are reused ZERO-COPY from the visited tree and
/// appear exactly once — no temporaries (C23 stays TempsLifted==0) and no
/// expression duplication;</item>
/// <item>hole arguments stay raw: unsigned byte/ushort/uint/ulong holes are
/// NOT wrapped with ToString (string.Format prints unsigned values
/// natively), null/Nullable holes get no ?? coalescing and no explicit
/// boxing is written;</item>
/// <item>the sole hole wrapping is a proven-safe ref-struct hole, emitted as
/// x.ToString() so it can materialize as an object argument (a ref struct
/// cannot be boxed into the params object[]);</item>
/// <item>format specifiers pass through verbatim — no C#→printf
/// translation, no TODO comments; unknown specifiers reach the BCL runtime
/// unchanged. Culture is never mentioned in the product: the emitted
/// string.Format resolves against the current culture, matching the default
/// handler (no CultureInfo.InvariantCulture / FormattableString.Invariant).</item>
/// </list>
/// </summary>
internal static class FormatEmitter
{
    /// <summary>
    /// IE-FORMAT for one visited site. <paramref name="visited"/> already has
    /// its inner interpolations rewritten (bottom-up); <paramref name="plan"/>
    /// segments are in the same content order as the original site.
    /// </summary>
    public static ExpressionSyntax EmitFormat(
        InterpolatedStringExpressionSyntax visited,
        InterpolationPlan plan)
    {
        var composite = new StringBuilder();
        var arguments = new List<ExpressionSyntax>();
        var holeIndex = 0;

        foreach (var content in visited.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    composite.Append(EscapeLiteralText(
                        InterpolationSyntaxFacts.GetTextValue(text)));
                    break;

                case InterpolationSyntax hole:
                {
                    var info = plan.Segments
                        .OfType<InterpolationSegment.Hole>()
                        .ElementAt(holeIndex)
                        .Info;
                    AppendPlaceholder(composite, holeIndex, info);
                    arguments.Add(PrepareArgument(hole.Expression, info));
                    holeIndex++;
                    break;
                }
            }
        }

        var compositeLiteral = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(composite.ToString()));

        var formatArguments = new List<ExpressionSyntax> { compositeLiteral };
        formatArguments.AddRange(arguments);

        return MakeStringFormat(formatArguments);
    }

    /// <summary>
    /// Composite brace escaping (R12): after ValueText decoding, only literal
    /// braces are special in the composite small language — double both.
    /// Percent and every other character pass through untouched.
    /// </summary>
    private static string EscapeLiteralText(string valueText)
        => valueText.Replace("{", "{{").Replace("}", "}}");

    private static void AppendPlaceholder(
        StringBuilder composite,
        int holeIndex,
        HoleInfo info)
    {
        composite.Append('{').Append(holeIndex);

        if (info.HasAlignmentClause)
        {
            // The analyze gate proved a compile-time constant (CS2J0514
            // otherwise); positive/negative/zero all pass through verbatim —
            // the BCL owns the space padding, no hand-rolled fill loops.
            composite.Append(',').Append(info.ConstantAlignment!.Value);
        }

        if (info.HasFormatClause)
        {
            // Raw specifier passthrough (D/F/E/X/N/G/P/M/R/unknown alike).
            composite.Append(':').Append(info.FormatText);
        }

        composite.Append('}');
    }

    private static ExpressionSyntax PrepareArgument(
        ExpressionSyntax expression,
        HoleInfo info)
    {
        // A ref struct cannot be boxed into the Format params object[]; the
        // analyze gate already proved this type overrides the parameterless
        // ToString (CS2J0513 otherwise). Everything else — unsigned numerics,
        // null references, Nullable<T>, custom IFormattable — stays raw.
        if (info.HasSafeRefStructToString)
            return InvokeParameterlessToString(expression);

        return expression;
    }

    private static InvocationExpressionSyntax InvokeParameterlessToString(
        ExpressionSyntax receiver)
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
    /// True when appending <c>.Member</c> directly would change the parse.
    /// Mirrors ConcatEmitter: tight-binding primaries stay unparenthesized.
    /// </summary>
    private static bool MemberAccessNeedsParentheses(ExpressionSyntax expression)
        => expression switch
        {
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
            PostfixUnaryExpressionSyntax => false,
            ConditionalAccessExpressionSyntax => false,
            InterpolatedStringExpressionSyntax => false,
            InitializerExpressionSyntax => false,
            _ => true,
        };

    private static InvocationExpressionSyntax MakeStringFormat(
        IReadOnlyList<ExpressionSyntax> arguments)
    {
        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                arguments.Select(static a => SyntaxFactory.Argument(a))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                SyntaxFactory.IdentifierName("Format")),
            argumentList);
    }
}
