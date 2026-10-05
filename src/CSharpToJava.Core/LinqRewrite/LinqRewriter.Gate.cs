using CSharpToJava.Core.LinqChannel;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqRewrite
{
    // D13 seam for the C26 LINQ channel. This partial carries only the gate
    // wiring; the routing knowledge lives in CSharpToJava.Core.LinqChannel and
    // Rules.cs stays free of any Queryable special-casing.
    public partial class LinqRewriter
    {
        // Null when the embedded E16 path constructs the rewriter: every gate
        // call short-circuits and behavior is byte-identical to the pre-C26
        // rewriter. The field is assigned from the extended constructor in
        // LinqRewriter.cs (a readonly field may be assigned by any partial's ctor).
        private readonly ILinqRewriteGate? _linqChannelGate;

        /// <summary>
        /// Observe-only hook at <c>TryVisitInvocationExpression</c>, before
        /// the <c>IsSupportedMethod</c> decision. MUST NOT affect the result:
        /// any observation failure is swallowed rather than perturbing E16.
        /// </summary>
        private void LinqChannelObserveInvocation(InvocationExpressionSyntax node)
        {
            if (_linqChannelGate is null)
            {
                return;
            }

            try
            {
                _linqChannelGate.ObserveInvocation(node);
            }
            catch
            {
                // Observation must never change rewrite output.
            }
        }

        /// <summary>
        /// Observe-only hook at <c>TryVisitForEachStatement</c>, before the
        /// collection's <c>IsSupportedMethod</c> decision.
        /// </summary>
        private void LinqChannelObserveForeachCollection(InvocationExpressionSyntax collection)
        {
            if (_linqChannelGate is null)
            {
                return;
            }

            try
            {
                _linqChannelGate.ObserveForeachCollection(collection);
            }
            catch
            {
                // Observation must never change rewrite output.
            }
        }

        /// <summary>
        /// M2 intercept hook for the invocation entry (design §4.2/§4.3):
        /// sits immediately before chain backtracking/rule matching. A true
        /// answer preserves the whole invocation subtree — receiver chain and
        /// quoted lambdas — so no node inside a preserved chain can reach the
        /// Enumerable expansion branch. Fail CLOSED: if the gate itself throws,
        /// the node is preserved rather than risked through the name-guess
        /// fallback. Observe-only gates answer false and never throw on this
        /// path, so the M1 behavior is unchanged.
        /// </summary>
        private bool LinqChannelShouldPreserveInvocation(InvocationExpressionSyntax node)
        {
            if (_linqChannelGate is null)
            {
                return false;
            }

            try
            {
                return _linqChannelGate.ShouldPreserveInvocation(node);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// M2 intercept hook for the foreach-rewrap entry, before the
        /// collection's <c>IsSupportedMethod</c> decision. Fail closed like
        /// <see cref="LinqChannelShouldPreserveInvocation"/>.
        /// </summary>
        private bool LinqChannelShouldPreserveForeachCollection(InvocationExpressionSyntax collection)
        {
            if (_linqChannelGate is null)
            {
                return false;
            }

            try
            {
                return _linqChannelGate.ShouldPreserveForeachCollection(collection);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// M3 LC0011 tripwire (design §4.3/§6): a preserved-route node must
        /// never enter the expansion branch. Null gate (embedded E16) = no-op,
        /// keeping that path byte-identical. The thrown
        /// <see cref="CSharpToJava.Core.LinqChannel.LinqRouteInvariantException"/>
        /// is intentionally NOT caught by the three-type wide catch in
        /// TryCatchVisitInvocationExpression — it fails the file (adapter rolls
        /// it back) instead of degrading to a skip.
        /// </summary>
        private void LinqChannelGuardEnterExpansion(InvocationExpressionSyntax node)
        {
            _linqChannelGate?.GuardEnterExpansion(node);
        }

        /// <summary>
        /// M3 LC0011 tripwire for the <c>n == null</c> syntax-name fallback
        /// inside <c>GetMethodFullName</c>. Invoked only for LINQ operator
        /// names whose fabricated identity the stock switch is about to return;
        /// with the channel gate attached this is an invariant violation (R2).
        /// Null gate = no-op, so embedded E16 behavior is unchanged.
        /// </summary>
        private void LinqChannelGuardSyntaxNameFallback(
            InvocationExpressionSyntax invocation, string syntaxName)
        {
            _linqChannelGate?.GuardSyntaxNameFallback(invocation, syntaxName);
        }
    }
}
