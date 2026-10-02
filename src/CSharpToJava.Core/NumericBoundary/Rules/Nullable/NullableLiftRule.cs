using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.NumericBoundary;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 NullableLift family rule.
/// <para>
/// <b>2026-10-02 implementation correction (Order=15).</b> The design text
/// (§3.2/decision 25) was written before C15 landed and assumed
/// C15=10/C17=20/C22=30. The landed truth is C17 IncrementTargets=10 (site
/// expansion) → C15 CheckedArithmetic=20 (checked post-sweep) → C34
/// DecimalOps=30 (decimal intake). C22 expands guards whose inner bare
/// <c>T</c> arithmetic must be post-swept by C15 (checked helpers,
/// decision 18) and whose <c>decimal</c> inner ops must be taken by C34
/// (decision 19), so the expander must run BEFORE both sweeps: Order=15
/// slots between C17 and C15. C17's classifier currently reports
/// LiftedNullable sites as CS2J5335; the handoff to NL-10/NL-11 happens in
/// M2 (design §11).
/// </para>
/// <para>
/// M0: read-only classification seam. Execute performs zero rewrites, emits
/// zero diagnostics, never touches the statistics buckets, and always returns
/// <c>MadeChanges=false</c> (output bytes unchanged). M1 adds NL-1…NL-5 plus
/// Pass2 hoisting; the NL-14 residual scanner below is wired in M1.
/// </para>
/// </summary>
internal sealed class NullableLiftRule : INumericBoundaryRule
{
    public string Id => "nullable-lift";

    public int Order => 15;

    public bool IsEnabled(NumericBoundaryOptions options) => options.EnableNullable;

    public NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context)
    {
        // Parse-only inputs carry no semantic model: every C22 decision is
        // semantic-model bound (hard invariant 4), so the rule is a no-op there.
        if (context.SemanticModel == null)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var analyzer = new NullableLiftAnalyzer(
            context.SemanticModel, context.Options, context.GetSharedAnalyzer());

        // M0 read-only probe: potential sites are counted into a local only.
        // No stats bucket moves, no diagnostic is emitted, the tree is not
        // visited by any rewriter. M1 replaces this with Analyze → Pass1 →
        // Pass2 → model rebind → NL-14 residual gate (see IncrementTargetRule).
        _ = analyzer.CountPotentialSites(context.Root);

        // NL-14 residual gate skeleton (NOT wired in M0 — no rewrite exists
        // yet, so the original tree trivially contains every lifted site).
        // M1 calls ScanResiduals on the rebound OUTPUT tree and emits
        // CS2J5357 (Warning; strict → Error) for anything surviving:
        //
        //   var residuals = ScanResiduals(context.SemanticModel, context.Root);
        //
        var residuals = ScanResiduals(context.SemanticModel, context.Root);
        _ = residuals.Count;

        return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
    }

    // ── NL-14 residual scanner skeleton (design §1.5 hard invariant 1) ──
    //
    // Wired in M1. The predicates are pure over IOperation so they are
    // unit-testable without the rule pipeline; the only text-based step is the
    // support-project fingerprint exemption (same discipline as C17 IT-13).

    /// <summary>
    /// Text fingerprints of released support projects: generated calls
    /// (<c>Cs2jUnsigned.*</c>, <c>dotnet.System.*</c>) are exempt from the
    /// user-scope residual gate.
    /// </summary>
    internal static bool IsSupportFingerprintExempt(SyntaxNode node)
    {
        var text = node.ToString();
        return text.Contains("Cs2jUnsigned", StringComparison.Ordinal)
            || text.Contains("dotnet.System", StringComparison.Ordinal);
    }

    /// <summary>
    /// NL-14 core predicate: true for a residual <b>built-in</b> lifted site
    /// (§1.5) — lifted binary/unary/increment/compound with a null/builtin
    /// operator method, a <c>Nullable&lt;T&gt;.GetValueOrDefault</c> call, or a
    /// <c>T? → T</c> unwrap / <c>S? → T?</c> lifted conversion. User-defined
    /// lifted operators (CS2J5353) are a separate failure class and never
    /// count as residual built-in sites.
    /// </summary>
    internal static bool IsResidualBuiltInLifted(IOperation operation, out string kind)
    {
        kind = "";
        switch (operation)
        {
            case NBOp.IBinaryOperation binary when binary.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(binary.OperatorMethod):
                kind = "binary";
                return true;
            case NBOp.IUnaryOperation unary when unary.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(unary.OperatorMethod):
                kind = "unary";
                return true;
            case NBOp.IIncrementOrDecrementOperation increment when increment.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(increment.OperatorMethod):
                kind = "increment";
                return true;
            case NBOp.ICompoundAssignmentOperation compound when compound.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(compound.OperatorMethod):
                kind = "compound";
                return true;
            case NBOp.IInvocationOperation invocation
                when invocation.TargetMethod is { Name: "GetValueOrDefault" } method
                    && NullableLiftAnalyzer.IsNullableType(method.ContainingType):
                kind = "get-value-or-default";
                return true;
            case NBOp.IConversionOperation conversion:
            {
                var classification = NullableLiftAnalyzer.ClassifyConversionOperation(conversion);
                if (classification.Quadrant is NullableConversionQuadrant.Unwrap
                    or NullableConversionQuadrant.UnwrapAndConvert
                    or NullableConversionQuadrant.Lifted)
                {
                    kind = "conversion-" + classification.Quadrant.ToString().ToLowerInvariant();
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Scan a bound tree for residual built-in lifted sites (NL-14). Each
    /// operation is visited once (a node and its implicit-conversion children
    /// can report the same operation); support-fingerprint nodes are exempt.
    /// M1 wires the result to CS2J5357; M0 only runs the probe through Execute
    /// and unit tests.
    /// </summary>
    internal static IReadOnlyList<(SyntaxNode Node, string Kind)> ScanResiduals(
        SemanticModel model, SyntaxNode root)
    {
        var residuals = new List<(SyntaxNode, string)>();
        var seen = new HashSet<IOperation>();

        foreach (var node in root.DescendantNodes())
        {
            if (model.GetOperation(node) is not { } operation || !seen.Add(operation))
            {
                continue;
            }

            if (IsResidualBuiltInLifted(operation, out var kind)
                && !IsSupportFingerprintExempt(node))
            {
                residuals.Add((node, kind));
            }
        }

        return residuals;
    }
}
