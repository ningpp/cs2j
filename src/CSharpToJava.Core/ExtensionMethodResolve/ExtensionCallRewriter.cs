using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Bottom-up rewriter (EM-CALL): for each bound node replaces
/// <c>r.M(args)</c> with <c>global::Host.M[&lt;ta&gt;]([ref/in] r, args)</c>,
/// reusing the receiver expression node unchanged (evaluation order preserved).
/// Method chains converge naturally from bottom-up traversal (EM-CHAIN).
///
/// M0 shell: the bindings table is always empty, so visiting any node returns
/// it unchanged and the file result stays Changed=false.
/// </summary>
internal sealed class ExtensionCallRewriter : CSharpSyntaxRewriter
{
    private readonly IReadOnlyDictionary<Microsoft.CodeAnalysis.SyntaxNode, ExtensionCallBinding> _bindingsByNode;
    private readonly ExtensionMethodResolveStatistics _statistics;

    public ExtensionCallRewriter(
        IReadOnlyDictionary<Microsoft.CodeAnalysis.SyntaxNode, ExtensionCallBinding> bindingsByNode,
        ExtensionMethodResolveStatistics statistics)
    {
        _bindingsByNode = bindingsByNode;
        _statistics = statistics;
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitInvocationExpression(
        Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax node)
    {
        var visited = base.VisitInvocationExpression(node);

        // M0: no bound nodes exist; the table lookup never succeeds. M1 emits
        // the qualified static invocation here (EM-CALL/EM-GEN/EM-REF).
        return _bindingsByNode.ContainsKey(node)
            ? throw new NotSupportedException("Extension call rewriting lands in M1.")
            : visited;
    }
}
