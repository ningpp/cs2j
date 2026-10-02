using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 collection-initializer emission (design §4 IN-3/IN-6/IN-7): constructor
/// arguments preserved verbatim by <see cref="ObjectInitRewriter.BuildConstruction"/>,
/// then one ordered <c>__ed_n.Add(args)</c> statement per bound Add item. The Add
/// target is the compiler-bound symbol (never a text guess of <c>add</c>/<c>put</c>);
/// arguments are reused verbatim, so extension Adds re-emit reduced syntax
/// (<c>tmp.Add(x)</c>) and params Adds re-emit one call per source element.
/// </summary>
internal static class CollectionInitRewriter
{
    /// <summary>Builds <c>receiver.Add(args);</c>.</summary>
    public static StatementSyntax BuildAddStatement(
        string receiver, IReadOnlyList<ExpressionSyntax> arguments)
    {
        var target = ObjectInitRewriter.MemberAccess(receiver, "Add");
        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                arguments.Select(SyntaxFactory.Argument)));

        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(target, argumentList));
    }
}
