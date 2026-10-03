using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Bottom-up rewriter (EM-CALL/EM-GEN/EM-GEN-HOST): for each bound node
/// replaces <c>r.M(args)</c> with <c>global::Host.M[&lt;ta&gt;](r, args)</c>.
///
/// Trivia policy (design §4.1): the receiver moves into the first argument
/// position and is reused as the SAME node (evaluation order/side effects
/// preserved, chains converge via bottom-up traversal); the member-access
/// leading trivia moves to the new invocation's first token. Method type
/// arguments are copied only when the source supplied them (EM-GEN): the
/// source <c>Name</c> node is either an IdentifierName (omitted — inference
/// gets re-run by the compiler on the explicit first parameter) or a
/// GenericName carrying exactly the source argument list. ref/in argument
/// modifiers are NOT emitted in M1 (EM-REF, M2).
/// </summary>
internal sealed class ExtensionCallRewriter : CSharpSyntaxRewriter
{
    /// <summary>Annotation placed on every emitted invocation (residual pairing / region formatting).</summary>
    public static readonly SyntaxAnnotation RewrittenAnnotation = new("C19", "ExtensionMethodRewrittenSite");

    private readonly IReadOnlyDictionary<SyntaxNode, ExtensionCallBinding> _bindingsByNode;

    public ExtensionCallRewriter(IReadOnlyDictionary<SyntaxNode, ExtensionCallBinding> bindingsByNode)
    {
        _bindingsByNode = bindingsByNode;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node) ?? node;

        if (!_bindingsByNode.TryGetValue(node, out var binding))
        {
            return visited;
        }

        // M1 bindings are ordinary member-access reduced calls only
        // (null-conditional MemberBinding sites are deferred to M2).
        var originalAccess = (MemberAccessExpressionSyntax)node.Expression;
        var visitedAccess = (MemberAccessExpressionSyntax)visited.Expression;

        // Take the receiver from the VISITED form: an inner extension call has
        // already been rewritten to its explicit static shape (EM-CHAIN).
        var receiver = visitedAccess.Expression;

        // The member-access leading trivia (trivia before the receiver, i.e.
        // indentation/start position) moves to the new invocation first token.
        var leadingTrivia = node.GetLeadingTrivia();

        // EM-GEN: reuse the source name node verbatim — IdentifierName when
        // type arguments were omitted, GenericName when they were explicit.
        var methodName = originalAccess.Name.WithoutTrivia();

        var hostName = ExtensionHostSyntaxFactory.BuildHostName(binding.Unreduced);
        var qualifiedMemberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            hostName,
            SyntaxFactory.Token(SyntaxKind.DotToken),
            methodName);

        var receiverArgument = SyntaxFactory.Argument(receiver.WithoutLeadingTrivia());
        var newArguments = visited.ArgumentList.Arguments.Insert(0, receiverArgument);
        var newArgumentList = SyntaxFactory.ArgumentList(newArguments);

        return SyntaxFactory.InvocationExpression(qualifiedMemberAccess, newArgumentList)
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(RewrittenAnnotation);
    }
}
