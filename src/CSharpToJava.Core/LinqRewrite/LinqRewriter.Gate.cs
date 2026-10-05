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
        /// M1 observe-only hook at <c>TryVisitInvocationExpression</c>, before
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
                // Observation must never change rewrite output (M1).
            }
        }

        /// <summary>
        /// M1 observe-only hook at <c>TryVisitForEachStatement</c>, before the
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
                // Observation must never change rewrite output (M1).
            }
        }
    }
}
