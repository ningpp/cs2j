using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Generic parameter mapping for block materialization (probe matrix):
/// the generated method's type parameter list is BLOCK parameters first,
/// then MEMBER parameters; block constraint clauses translate verbatim
/// alongside member clauses. Call sites keep their source name node, so
/// explicit argument lists (omitted / full n+m) are preserved as written —
/// the member-only-arguments shape is CS1061 and never reaches the rewriter.
/// </summary>
internal static class GenericParameterMapping
{
    /// <summary>
    /// Build the merged <c>&lt;TBlock..., TMember...&gt;</c> type parameter
    /// list; null when both are empty.
    /// </summary>
    public static TypeParameterListSyntax? MergeTypeParameters(
        IReadOnlyList<TypeParameterSyntax> blockParameters,
        IReadOnlyList<TypeParameterSyntax> memberParameters)
    {
        var count = blockParameters.Count + memberParameters.Count;
        if (count == 0)
        {
            return null;
        }

        var nodes = new List<TypeParameterSyntax>(count);
        nodes.AddRange(blockParameters.Select(WithoutConstraintsTrivia));
        nodes.AddRange(memberParameters.Select(WithoutConstraintsTrivia));
        return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(nodes));
    }

    /// <summary>Merge block + member constraint clauses (names are disjoint by C# rules).</summary>
    public static SyntaxList<TypeParameterConstraintClauseSyntax> MergeConstraints(
        IReadOnlyList<TypeParameterConstraintClauseSyntax> blockConstraints,
        IReadOnlyList<TypeParameterConstraintClauseSyntax> memberConstraints)
        => SyntaxFactory.List(blockConstraints.Concat(memberConstraints));

    /// <summary>
    /// Build the first parameter of the materialized method:
    /// <c>this [ref|in] [scoped] ReceiverType receiverName</c>. The original
    /// receiver name is preserved so method bodies need zero renaming.
    /// </summary>
    public static ParameterSyntax BuildReceiverParameter(BlockReceiverInfo receiver)
    {
        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.ThisKeyword),
        };

        switch (receiver.RefKind)
        {
            case RefKind.Ref:
            case RefKind.Out:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                break;
            case RefKind.In:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InKeyword));
                break;
        }

        if (receiver.ScopedKind is ScopedKind.ScopedValue or ScopedKind.ScopedRef)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.ScopedKeyword));
        }

        var receiverType = SyntaxFactory.ParseTypeName(
            receiver.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        return SyntaxFactory.Parameter(
            default,
            SyntaxFactory.TokenList(modifiers),
            receiverType,
            SyntaxFactory.Identifier(receiver.Name),
            null);
    }

    private static TypeParameterSyntax WithoutConstraintsTrivia(TypeParameterSyntax parameter)
        => parameter.WithoutTrivia();
}
