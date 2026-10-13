using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 NullableLift family rule (Order=15, between C17=10 and C15=20).
/// <para>
/// The landed rule order is C17 IncrementTargets=10 → C22 NullableLift=15 →
/// C15 CheckedArithmetic=20 → C34 DecimalOps=30: C22 expands HasValue guards
/// whose inner bare <c>T</c> arithmetic must then be post-swept by C15
/// (checked helpers, decision 18) and whose <c>decimal</c> inner ops are
/// taken by C34 (decision 19). C17 hands LiftedNullable increment sites
/// silently over to this rule when EnableNullable is on (NL-10/NL-11).
/// </para>
/// <para>
/// M1 owns NL-1…NL-5 (lifted arithmetic/unary/relational/equality/bool-logic
/// on numeric and bool domains) plus the Pass2 temporary hoist; M2 completes
/// NL-6…NL-11 (GetValueOrDefault, unwrap/lifted conversions, enum backstop,
/// lifted increment/compound) and flips the NL-14 gate to full scope: every
/// built-in lifted Nullable&lt;T&gt; shape surviving in the rebound output is
/// a CS2J5357 violation (design §1.5 invariant 1 / §4.3 NL-14 / §7.5-1).
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

        var originalRoot = context.Root;

        // Expression-lambda statement linearization (decision 21): turn
        // &&-predicate expression lambdas that contain lifted sites into
        // statement lambdas with early-false guards, so the sites gain a
        // return anchor inside the (now) block scope. Speculative: a residual
        // CS2J5355 below still rolls the WHOLE file back, and the failure
        // result returns the untouched original root.
        var linearizedRoot = NullableLambdaLinearizer.Linearize(
            context.Root, context.SemanticModel, out var linearized);
        if (linearized)
        {
            var linearizedModel = context.RebindModel(linearizedRoot);
            if (linearizedModel == null)
            {
                context.Diagnostics.Add(new NumericBoundaryDiagnostic(
                    NumericBoundarySeverity.Error,
                    PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
                    "numeric-boundary rule 'nullable-lift' produced a tree that fails to reparse while " +
                    "linearizing expression lambdas; the original file was preserved " +
                    $"({PreprocessDiagnosticCodes.NumericBoundaryBuildFailed})."));
                context.Stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryBuildFailed] = 1;
                return new NumericBoundaryRuleResult(originalRoot, MadeChanges: false);
            }
        }

        var analyzer = new NullableLiftAnalyzer(
            context.SemanticModel, context.Options, context.GetSharedAnalyzer());

        // Pass 1: bottom-up guard expansion + temp collection over the
        // (possibly lambda-linearized) bound tree; errors surface during visit.
        var rewriter = new NullableLiftRewriter(
            context.SemanticModel,
            context.Options,
            context.Stats.Nullable,
            context.Diagnostics,
            analyzer);

        var visitedRoot = (CompilationUnitSyntax)rewriter.Visit(context.Root)!;

        // Any site error (CS2J5352-5356): never touch the tree; NB-12 rolls
        // the file back to the original bytes via the Error diagnostic.
        if (rewriter.HasErrorDiagnostic)
        {
            return new NumericBoundaryRuleResult(originalRoot, MadeChanges: false);
        }

        if (!rewriter.MadeChanges)
        {
            // Lambda linearization without a guard expansion never happens
            // (a candidate body always contains a rewritten lifted site).
            return new NumericBoundaryRuleResult(originalRoot, MadeChanges: false);
        }

        // Pass 2: hoist single-evaluation temp declarations to anchors.
        var newRoot = NullableLiftLifter.Apply(visitedRoot, rewriter.Pending);

        // Rebind before the residual gate (and the C15/C34 sweeps).
        var reboundModel = context.RebindModel(newRoot);
        if (reboundModel == null)
        {
            context.Diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Error,
                PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
                "numeric-boundary rule 'nullable-lift' produced a tree that fails to reparse while " +
                "rebinding the semantic model; the original file was preserved " +
                $"({PreprocessDiagnosticCodes.NumericBoundaryBuildFailed})."));
            context.Stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryBuildFailed] = 1;
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        ApplyResidualGate(
            reboundModel,
            context.Root,
            context.Options,
            context.Stats.Nullable,
            context.Diagnostics);

        EmitInfo(context.Stats.Nullable, context.Diagnostics);

        return new NumericBoundaryRuleResult(context.Root, MadeChanges: true);
    }

    /// <summary>
    /// NL-14 full-scope residual gate (M2): rescan the rebound OUTPUT tree and
    /// report CS2J5357 (strict → Error, otherwise Warning) for every surviving
    /// built-in lifted <c>Nullable&lt;T&gt;</c> shape — binary/unary (NL-1…5),
    /// increment/compound (NL-10/11), <c>GetValueOrDefault</c> (NL-6) and
    /// unwrap/lifted conversions (NL-7/8). Aggregation text keeps at most five
    /// site samples. Not owned (hence not gated): E04 guard conditionals
    /// (conditional product, not a lifted binding), wrapping conversions
    /// (Quadrant.Wrap), <c>.HasValue/.Value</c> member access,
    /// <c>Nullable.GetUnderlyingType</c>, <c>typeof(T?)</c>, user-defined
    /// lifted operators/conversions (rolled back via CS2J5353), C41 special
    /// numerics (CS2J5356) and released support-project fingerprints
    /// (<c>Cs2jUnsigned.*</c>/<c>dotnet.System.*</c>). The gate runs only on
    /// rewritten trees, so fully clean files are never scanned.
    /// </summary>
    private static void ApplyResidualGate(
        SemanticModel model,
        CompilationUnitSyntax root,
        NumericBoundaryOptions options,
        NullableLiftStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var residualSites = ScanResiduals(model, root);

        if (residualSites.Count == 0)
        {
            return;
        }

        var sampleTexts = residualSites
            .Take(5)
            .Select(site => NullableLiftSyntaxFacts.SiteText(site.Node))
            .ToList();

        var severity = options.Strict
            ? NumericBoundarySeverity.Error
            : NumericBoundarySeverity.Warning;
        diagnostics.Add(new NumericBoundaryDiagnostic(
            severity,
            PreprocessDiagnosticCodes.NullableLiftResidual,
            NullableLiftDiagnostics.Residual(string.Join(", ", sampleTexts))));
        var key = PreprocessDiagnosticCodes.NullableLiftResidual;
        stats.DeferredByReason[key] = stats.DeferredByReason.TryGetValue(key, out var count)
            ? count + 1
            : 1;
    }

    /// <summary>
    /// CS2J5350/CS2J5351 aggregated, verbose-only Info diagnostics (design
    /// §5-3): the site breakdown lists every nonzero M1+M2 bucket in fixed
    /// order <c>arith/unary/rel/eq/bool/gov/conv/inc</c> plus enum-backstop
    /// and co-run checked-inner context; the temp breakdown names only the
    /// hoisted roles (<c>a</c>/<c>b</c>/<c>o</c>/<c>d</c> plus the M3
    /// decision-17 <c>t</c>/<c>r</c>/<c>i</c> complex-RMW roles) that occurred.
    /// </summary>
    private static void EmitInfo(
        NullableLiftStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var convSites = stats.UnwrapConversions + stats.LiftedConversions;
        var incrementSites = stats.LiftedIncrementPrefixSites
            + stats.LiftedIncrementPostfixSites
            + stats.LiftedIncrementCompoundSites;
        var totalSites = stats.LiftedBinarySites
            + stats.LiftedUnarySites
            + stats.LiftedRelationalSites
            + stats.LiftedEqualitySites
            + stats.LiftedBoolLogicSites
            + stats.GetValueOrDefaultSites
            + convSites
            + incrementSites
            + stats.NullableEnumBackstopSites
            + stats.GuardedCheckedInnerSites;
        if (totalSites == 0)
        {
            return;
        }

        var breakdown = new List<string>();
        if (stats.LiftedBinarySites > 0)
        {
            breakdown.Add($"{stats.LiftedBinarySites} arith");
        }

        if (stats.LiftedUnarySites > 0)
        {
            breakdown.Add($"{stats.LiftedUnarySites} unary");
        }

        if (stats.LiftedRelationalSites > 0)
        {
            breakdown.Add($"{stats.LiftedRelationalSites} rel");
        }

        if (stats.LiftedEqualitySites > 0)
        {
            breakdown.Add($"{stats.LiftedEqualitySites} eq");
        }

        if (stats.LiftedBoolLogicSites > 0)
        {
            breakdown.Add($"{stats.LiftedBoolLogicSites} bool");
        }

        if (stats.GetValueOrDefaultSites > 0)
        {
            breakdown.Add($"{stats.GetValueOrDefaultSites} gov");
        }

        if (convSites > 0)
        {
            breakdown.Add($"{convSites} conv");
        }

        if (incrementSites > 0)
        {
            breakdown.Add($"{incrementSites} inc");
        }

        if (stats.NullableEnumBackstopSites > 0)
        {
            breakdown.Add($"{stats.NullableEnumBackstopSites} enum backstop");
        }

        if (stats.GuardedCheckedInnerSites > 0)
        {
            breakdown.Add($"{stats.GuardedCheckedInnerSites} checked-inner");
        }

        diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Info,
            PreprocessDiagnosticCodes.NullableLiftSiteInfo,
            NullableLiftDiagnostics.SiteInfo(totalSites, string.Join(", ", breakdown))));

        if (stats.TempsLifted > 0)
        {
            var roles = new List<string>();
            if (stats.TempsLeftRole > 0)
            {
                roles.Add($"{stats.TempsLeftRole} a");
            }

            if (stats.TempsRightRole > 0)
            {
                roles.Add($"{stats.TempsRightRole} b");
            }

            if (stats.TempsOldRole > 0)
            {
                roles.Add($"{stats.TempsOldRole} o");
            }

            if (stats.TempsDefaultRole > 0)
            {
                roles.Add($"{stats.TempsDefaultRole} d");
            }

            if (stats.TempsTargetRole > 0)
            {
                roles.Add($"{stats.TempsTargetRole} t");
            }

            if (stats.TempsReceiverRole > 0)
            {
                roles.Add($"{stats.TempsReceiverRole} r");
            }

            if (stats.TempsIndexRole > 0)
            {
                roles.Add($"{stats.TempsIndexRole} i");
            }

            diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Info,
                PreprocessDiagnosticCodes.NullableLiftTempInfo,
                NullableLiftDiagnostics.TempInfo(stats.TempsLifted, string.Join(", ", roles))));
        }
    }

    // ── NL-14 full-scope residual scanner; the live M2 gate above consumes
    // the same predicate, and M0 probe tests exercise it directly ──

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
    /// NL-14 full-scope predicate across every built-in lifted shape:
    /// lifted binary/unary/increment/compound with a built-in operator, a
    /// <c>Nullable&lt;T&gt;.GetValueOrDefault</c> call, or a T?→T unwrap /
    /// S?→T? lifted conversion. The live M2 gate and the probe tests share
    /// this predicate.
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

    /// <summary>Full-scope residual scan used by the live NL-14 gate and probe tests.</summary>
    internal static IReadOnlyList<(SyntaxNode Node, string Kind)> ScanResiduals(
        SemanticModel model, SyntaxNode root)
    {
        var residuals = new List<(SyntaxNode, string)>();
        var seen = new HashSet<IOperation>();

        foreach (var node in root.DescendantNodes())
        {
            if (model.GetOperation(node) is not { } operation)
            {
                continue;
            }

            // Enumerate the whole bound operation subtree, not just the
            // operation returned for the syntax node: operations hidden inside
            // a conditional access (e.g. the GetValueOrDefault invocation of
            // `Get()?.GetValueOrDefault()` — its WhenNotNull invocation shares
            // the conditional-access syntax node and is never returned by
            // GetOperation) would otherwise survive the gate unscanned.
            var pending = new Stack<IOperation>();
            pending.Push(operation);
            while (pending.Count > 0)
            {
                var descendant = pending.Pop();
                if (!seen.Add(descendant) || descendant.Syntax == null)
                {
                    continue;
                }

                if (IsResidualBuiltInLifted(descendant, out var kind)
                    && !IsSupportFingerprintExempt(descendant.Syntax))
                {
                    residuals.Add((descendant.Syntax, kind));
                }

                foreach (var child in descendant.ChildOperations)
                {
                    if (child != null)
                    {
                        pending.Push(child);
                    }
                }
            }
        }

        return residuals;
    }
}
