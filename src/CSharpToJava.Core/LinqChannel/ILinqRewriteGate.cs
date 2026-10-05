using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Seam between the C26 channel and the embedded <c>LinqRewriter</c> (D13).
/// </summary>
/// <remarks>
/// M2 contract — SWITCHABLE (design §4.2/§4.3):
/// <list type="bullet">
/// <item><b>observe-only mode</b> (<c>LinqChannelOptions.EnableQueryableGate
/// = false</c>): implementations classify/count at the Observe* hooks and
/// every <see cref="ShouldPreserveInvocation"/>/
/// <see cref="ShouldPreserveForeachCollection"/> answer is false. The rewriter
/// output stays byte-identical to the ungated E16 run (M1 behavior).</item>
/// <item><b>intercept mode</b> (option true, the default): a true answer makes
/// the rewriter early-exit with the node (and its whole subtree, quoted
/// lambdas included) untouched. Enumerable/NonLinq chains pass through and
/// expand exactly as E16; Queryable/ExpressionArgument/Mixed/Ambiguous chains
/// are preserved (Ambiguous additionally reports LC0010).</item>
/// </list>
/// A null gate (the embedded E16 construction) short-circuits every check —
/// behavior is byte-identical to the pre-C26 rewriter. M3 removes the
/// observe-only/off switch and turns route violations into the LC0011 hard
/// invariant; the fallback-unreachable assertion stays M3 by design.
/// </remarks>
public interface ILinqRewriteGate
{
    /// <summary>
    /// Called from <c>LinqRewriter.TryVisitInvocationExpression</c> immediately
    /// before the <c>IsSupportedMethod</c> support gate (via the invocation
    /// visitor entry). Observation must never change the rewrite outcome.
    /// </summary>
    void ObserveInvocation(InvocationExpressionSyntax invocation);

    /// <summary>
    /// Called from <c>LinqRewriter.TryVisitForEachStatement</c> on the
    /// foreach collection, immediately before its <c>IsSupportedMethod</c>
    /// gate (the foreach-rewrap entry must not bypass observation).
    /// </summary>
    void ObserveForeachCollection(InvocationExpressionSyntax collection);

    /// <summary>
    /// Intercept-mode hook at the invocation entry. True preserves the whole
    /// invocation subtree (receiver chain + argument lambdas) without
    /// descending. Observe-only implementations always answer false.
    /// </summary>
    bool ShouldPreserveInvocation(InvocationExpressionSyntax invocation);

    /// <summary>
    /// Intercept-mode hook at the foreach-rewrap entry. True freezes the
    /// collection chain expression while the foreach body is still visited
    /// normally. Observe-only implementations always answer false.
    /// </summary>
    bool ShouldPreserveForeachCollection(InvocationExpressionSyntax collection);
}
