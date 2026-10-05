using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Seam between the C26 channel and the embedded <c>LinqRewriter</c> (D13).
/// </summary>
/// <remarks>
/// M3 contract — PERMANENTLY INTERCEPTING (design §4.2/§4.3/§4.6):
/// inside the channel every <see cref="ShouldPreserveInvocation"/>/
/// <see cref="ShouldPreserveForeachCollection"/> answer follows the per-file
/// route plan plus the quoted-expression region set: Queryable/
/// ExpressionArgument/Mixed/Ambiguous chains and ANY invocation lexically
/// inside an <c>Expression&lt;&gt;</c>-quoted lambda are frozen node for node;
/// only Enumerable/NonLinq nodes outside quoted regions expand, exactly as E16.
/// <para>
/// The M1/M2 observe-only mode was removed together with
/// <c>LinqChannelOptions.EnableQueryableGate</c> (the three gates are
/// unclosable). A null gate (the embedded E16 construction) still short-
/// circuits every check — behavior is byte-identical to the pre-C26 rewriter,
/// including the syntax-name fallback.
/// </para>
/// <para>
/// The two Guard* hooks are LC0011 anti-regression tripwires that only a
/// non-null gate implements; they throw <see cref="LinqRouteInvariantException"/>
/// instead of returning when a preserved-route node enters the expansion
/// branch or the <c>n == null</c> syntax-name fallback is touched for a LINQ
/// operator in chain context.
/// </para>
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
    /// Intercept hook at the invocation entry. True preserves the whole
    /// invocation subtree (receiver chain + argument lambdas) without
    /// descending.
    /// </summary>
    bool ShouldPreserveInvocation(InvocationExpressionSyntax invocation);

    /// <summary>
    /// Intercept hook at the foreach-rewrap entry. True freezes the
    /// collection chain expression while the foreach body is still visited
    /// normally.
    /// </summary>
    bool ShouldPreserveForeachCollection(InvocationExpressionSyntax collection);

    /// <summary>
    /// LC0011 tripwire at the expansion branch (design §4.3/§6). Called after
    /// the preserve hook answered false, immediately before
    /// <c>IsSupportedMethod</c> chain backtracking. Throws
    /// <see cref="LinqRouteInvariantException"/> when the node's route decision
    /// is nevertheless a preserved route (Queryable/Expression/Mixed/Ambiguous
    /// or inside a quoted region). Unreachable in correct operation.
    /// </summary>
    void GuardEnterExpansion(InvocationExpressionSyntax invocation);

    /// <summary>
    /// LC0011 tripwire for the <c>GetMethodFullName</c> syntax-name fallback
    /// (design §4.3, R2). Called only when the bound symbol is null and the
    /// switch is about to fabricate an <c>IEnumerable&lt;TSource&gt;</c>
    /// identity from a LINQ operator syntax name while a gate is attached;
    /// throws <see cref="LinqRouteInvariantException"/>. The embedded E16
    /// null-gate path never calls this and keeps the fallback byte-identical.
    /// </summary>
    void GuardSyntaxNameFallback(InvocationExpressionSyntax invocation, string syntaxName);
}
