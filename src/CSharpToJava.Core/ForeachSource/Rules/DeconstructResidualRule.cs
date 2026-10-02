using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Ruling 1 (M2): a deconstruction foreach whose element exposes an
/// instance <c>Deconstruct</c> with all-<c>out</c> parameters is lowered to a
/// plain foreach over a synthetic entry variable plus an explicit
/// <c>__e.Deconstruct(out …)</c> call:
/// <code>
/// foreach ((int x, var y) in src) { body }
/// ↓
/// foreach (var __cs2jFsEntry in src)
/// {
///     __cs2jFsEntry.Deconstruct(out int x, out var y);
///     body
/// }
/// </code>
/// Nested designations are flattened recursively: each nested tuple slot
/// binds to a fresh <c>__cs2jFsInnerN</c> in the outer call followed by its
/// own Deconstruct call (delegated to ordinary compiler overload
/// resolution). Discards render as <c>out _</c>.
/// </summary>
internal sealed class DeconstructResidualRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachVariableStatementSyntax visited
            || site.Family != ForeachSourceFamily.Deconstruct)
        {
            return null;
        }

        var entryName = context.FreshName(ForeachSourceSyntaxFacts.EntryName, visited);
        var nestedCalls = new List<StatementSyntax>();
        var arguments = BuildTopArguments(visited.Variable, context, visited, nestedCalls);

        var prepend = new List<StatementSyntax>
        {
            BuildDeconstructStatement(SyntaxFactory.IdentifierName(entryName), arguments),
        };
        prepend.AddRange(nestedCalls);
        var body = ForeachSourceSyntaxFacts.PrependStatements(visited.Statement, prepend);

        var rewritten = SyntaxFactory.ForEachStatement(
            visited.AwaitKeyword,
            visited.ForEachKeyword,
            visited.OpenParenToken,
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(entryName),
            visited.InKeyword,
            visited.Expression,
            visited.CloseParenToken,
            body)
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithTrailingTrivia(visited.GetTrailingTrivia());

        context.Changed = true;
        context.Statistics.DeconstructResidualLowered++;
        return new ForeachSourceRuleOutcome(rewritten, Changed: true);
    }

    /// <summary>
    /// Build the out-argument list for the entry Deconstruct call, covering
    /// both the declaration form (<c>var (x, y)</c>, <c>(int, string) (x, y)</c>)
    /// and the tuple expression form (<c>(int x, var y, z, _)</c>).
    /// </summary>
    private static List<ArgumentSyntax> BuildTopArguments(
        ExpressionSyntax variable,
        ForeachSourceRewriteContext context,
        ForEachVariableStatementSyntax node,
        List<StatementSyntax> nestedCalls)
    {
        if (variable is DeclarationExpressionSyntax
            {
                Designation: ParenthesizedVariableDesignationSyntax designation,
            } declaration)
        {
            var perPositionTypes = declaration.Type is TupleTypeSyntax tupleType
                ? tupleType.Elements.Select(e => e.Type).ToArray()
                : null;

            return designation.Variables
                .Select((d, i) => DesignationToOutArgument(
                    d,
                    perPositionTypes != null && i < perPositionTypes.Length
                        ? perPositionTypes[i]
                        : null,
                    context, node, nestedCalls))
                .ToList();
        }

        if (variable is TupleExpressionSyntax tuple)
        {
            return tuple.Arguments
                .Select(a => ExpressionToOutArgument(
                    a.Expression, context, node, nestedCalls))
                .ToList();
        }

        // Uncommon single-target form: pass the expression through as-is.
        return new List<ArgumentSyntax> { OutArgument(variable.WithoutTrivia()) };
    }

    private static ArgumentSyntax DesignationToOutArgument(
        VariableDesignationSyntax designation,
        TypeSyntax? explicitType,
        ForeachSourceRewriteContext context,
        ForEachVariableStatementSyntax node,
        List<StatementSyntax> nestedCalls)
    {
        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                return OutArgument(SyntaxFactory.DeclarationExpression(
                    explicitType ?? SyntaxFactory.IdentifierName("var"),
                    single.WithoutTrivia()));

            case DiscardDesignationSyntax:
                // out _ — no variable introduced.
                return OutArgument(SyntaxFactory.IdentifierName("_"));

            case ParenthesizedVariableDesignationSyntax nested:
                return NestedSlotToOutArgument(nested, context, node, nestedCalls);

            default:
                return OutArgument(SyntaxFactory.IdentifierName("_"));
        }
    }

    private static ArgumentSyntax ExpressionToOutArgument(
        ExpressionSyntax expression,
        ForeachSourceRewriteContext context,
        ForEachVariableStatementSyntax node,
        List<StatementSyntax> nestedCalls)
    {
        switch (expression)
        {
            case DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax }:
                return OutArgument(expression.WithoutTrivia());

            case DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax }:
                return OutArgument(SyntaxFactory.IdentifierName("_"));

            case DeclarationExpressionSyntax
            {
                Designation: ParenthesizedVariableDesignationSyntax nested,
            }:
                return NestedSlotToOutArgument(nested, context, node, nestedCalls);

            case TupleExpressionSyntax nestedTuple:
            {
                var innerName = context.FreshName(ForeachSourceSyntaxFacts.InnerName, node);
                var innerArgs = nestedTuple.Arguments
                    .Select(a => ExpressionToOutArgument(
                        a.Expression, context, node, nestedCalls))
                    .ToList();
                nestedCalls.Add(BuildDeconstructStatement(
                    SyntaxFactory.IdentifierName(innerName), innerArgs));
                return OutArgument(DeclareVar(innerName));
            }

            default:
                // Existing local (incl. discard "_") passed by out.
                return OutArgument(expression.WithoutTrivia());
        }
    }

    private static ArgumentSyntax NestedSlotToOutArgument(
        ParenthesizedVariableDesignationSyntax nested,
        ForeachSourceRewriteContext context,
        ForEachVariableStatementSyntax node,
        List<StatementSyntax> nestedCalls)
    {
        var innerName = context.FreshName(ForeachSourceSyntaxFacts.InnerName, node);
        var innerArgs = nested.Variables
            .Select(d => DesignationToOutArgument(
                d, explicitType: null, context, node, nestedCalls))
            .ToList();
        nestedCalls.Add(BuildDeconstructStatement(
            SyntaxFactory.IdentifierName(innerName), innerArgs));
        return OutArgument(DeclareVar(innerName));
    }

    /// <summary><c>out var name</c> — declares the synthetic inner slot variable.</summary>
    private static ExpressionSyntax DeclareVar(string name)
        => SyntaxFactory.DeclarationExpression(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(name)));

    private static ExpressionStatementSyntax BuildDeconstructStatement(
        ExpressionSyntax receiver, IReadOnlyList<ArgumentSyntax> arguments)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    SyntaxFactory.IdentifierName("Deconstruct")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(arguments))));

    private static ArgumentSyntax OutArgument(ExpressionSyntax expression)
        => SyntaxFactory.Argument(
            default,
            SyntaxFactory.Token(SyntaxKind.OutKeyword),
            expression);
}
