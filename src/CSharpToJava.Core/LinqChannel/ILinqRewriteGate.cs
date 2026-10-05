using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Seam between the C26 channel and the embedded <c>LinqRewriter</c> (D13).
/// </summary>
/// <remarks>
/// M1 contract — OBSERVE ONLY. Implementations may classify and count, but the
/// rewriter ignores every return value and never changes its rewrite decision
/// based on this interface; channel output must remain byte-identical to the
/// ungated E16 run. M3 turns this into a hard preserve gate (LC0011 invariant);
/// the M1 shape deliberately exposes observation hooks only.
/// </remarks>
public interface ILinqRewriteGate
{
    /// <summary>
    /// Called from <c>LinqRewriter.TryVisitInvocationExpression</c> immediately
    /// before the <c>IsSupportedMethod</c> support gate.
    /// </summary>
    void ObserveInvocation(InvocationExpressionSyntax invocation);

    /// <summary>
    /// Called from <c>LinqRewriter.TryVisitForEachStatement</c> on the
    /// foreach collection, immediately before its <c>IsSupportedMethod</c>
    /// gate (the foreach-rewrap entry must not bypass observation).
    /// </summary>
    void ObserveForeachCollection(InvocationExpressionSyntax collection);
}
