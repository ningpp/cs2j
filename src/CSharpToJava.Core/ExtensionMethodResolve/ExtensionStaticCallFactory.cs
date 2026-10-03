using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Builds <c>global::Host.M[&lt;ta&gt;]([ref/in] receiver, args)</c> for every
/// rewrite shape (EM-CALL/EM-GEN/EM-GEN-HOST/EM-REF/EM-COND/EM-GROUP). The
/// host name comes from the constructed unreduced symbol
/// (<see cref="ExtensionHostSyntaxFactory"/>); the method name node is reused
/// verbatim from the source binding (GenericName keeps explicit type
/// arguments, EM-GEN); only the receiver argument gains a ref/in modifier
/// when the first parameter demands one (scoped emits nothing).
/// </summary>
internal static class ExtensionStaticCallFactory
{
    public static InvocationExpressionSyntax Build(
        IMethodSymbol unreduced,
        SimpleNameSyntax sourceMethodName,
        ExpressionSyntax receiver,
        RefKind receiverRefKind,
        ScopedKind receiverScopedKind,
        SeparatedSyntaxList<ArgumentSyntax> remainingArguments,
        IEnumerable<SyntaxAnnotation>? annotations = null)
    {
        var hostName = ExtensionHostSyntaxFactory.BuildHostName(unreduced);
        var qualifiedMemberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            hostName,
            SyntaxFactory.Token(SyntaxKind.DotToken),
            sourceMethodName.WithoutTrivia());

        var receiverArgument = SyntaxFactory
            .Argument(receiver.WithoutLeadingTrivia())
            .WithRefKindKeyword(BuildReceiverModifier(receiverRefKind, receiverScopedKind));

        var arguments = remainingArguments.Insert(0, receiverArgument);
        var argumentList = SyntaxFactory.ArgumentList(arguments);

        var invocation = SyntaxFactory.InvocationExpression(qualifiedMemberAccess, argumentList);
        if (annotations is not null)
        {
            invocation = invocation.WithAdditionalAnnotations(annotations.ToArray());
        }

        return invocation;
    }

    /// <summary>
    /// Receiver modifier from the unreduced first-parameter facts:
    /// <c>this ref</c>/<c>this out</c> → ref; <c>this in</c> → NO keyword
    /// (explicit <c>in</c> on an rvalue is CS8156; an unadorned argument is
    /// implicitly passed by readonly reference, so rvalues stay legal);
    /// <c>scoped</c>/<c>scoped ref</c> escape metadata never reaches call
    /// syntax (scoped ref still takes the ref keyword).
    /// </summary>
    private static SyntaxToken BuildReceiverModifier(RefKind refKind, ScopedKind scopedKind)
    {
        // scoped VALUE with RefKind.None is metadata-only, no keyword.
        _ = scopedKind;
        return refKind switch
        {
            RefKind.Ref or RefKind.Out => SyntaxFactory.Token(SyntaxKind.RefKeyword),
            _ => SyntaxFactory.Token(SyntaxKind.None),
        };
    }
}
