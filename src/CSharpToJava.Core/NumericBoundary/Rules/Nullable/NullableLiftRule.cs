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
/// taken by C34 (decision 19). C17 still reports LiftedNullable increment
/// sites as CS2J5335 until the NL-10/NL-11 handoff lands in M2.
/// </para>
/// <para>
/// M1 owns NL-1…NL-5 (lifted arithmetic/unary/relational/equality/bool-logic
/// on numeric and bool domains) plus the Pass2 temporary hoist. The NL-14
/// residual gate is staged: only M1-owned shapes can trigger CS2J5357.
/// Increment/compound (NL-10/11), GetValueOrDefault (NL-6), conversions
/// (NL-7/8) and enum-domain bit operations (NL-9) are left for M2 and pass
/// through untouched and undiagnosed.
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

        // Pass 1: bottom-up guard expansion + temp collection over the
        // ORIGINAL bound tree (errors are discovered during the visit).
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
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        if (!rewriter.MadeChanges)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
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

        var reboundRoot = context.Root;
        ApplyResidualGate(
            reboundModel,
            reboundRoot,
            context.Options,
            context.Stats.Nullable,
            context.Diagnostics,
            context.GetSharedAnalyzer());

        EmitInfo(context.Stats.Nullable, context.Diagnostics);

        return new NumericBoundaryRuleResult(reboundRoot, MadeChanges: true);
    }

    /// <summary>
    /// NL-14 staged M1 residual gate: rescan the rebound OUTPUT tree and
    /// report CS2J5357 (strict → Error, otherwise Warning) only for lifted
    /// built-in shapes M1 actually owns — binary/unary lifted sites on the
    /// integer/unsigned/floating/decimal/bool domains in the arithmetic,
    /// relational, equality or three-valued-logic flavors. The raw
    /// <c>x == null</c> shape cannot survive (it rewrites to HasValue).
    /// Explicitly NOT gated in M1: increment/compound (NL-10/11),
    /// GetValueOrDefault (NL-6), nullable conversions (NL-7/8), enum-domain
    /// bit operations/<c>~</c> (NL-9), C41 special numerics (their own
    /// CS2J5356 class) and support-project fingerprints.
    /// </summary>
    private static void ApplyResidualGate(
        SemanticModel model,
        CompilationUnitSyntax root,
        NumericBoundaryOptions options,
        NullableLiftStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        NumericBoundaryAnalyzer sharedAnalyzer)
    {
        var reboundAnalyzer = new NullableLiftAnalyzer(model, options, sharedAnalyzer);

        var residualSites = new List<string>();
        var seen = new HashSet<IOperation>();
        foreach (var node in root.DescendantNodes())
        {
            if (model.GetOperation(node) is not { } operation || !seen.Add(operation))
            {
                continue;
            }

            if (!IsM1OwnedResidual(reboundAnalyzer, node, operation)
                || IsSupportFingerprintExempt(node))
            {
                continue;
            }

            if (residualSites.Count < 5)
            {
                residualSites.Add(NullableLiftSyntaxFacts.SiteText(node));
            }
        }

        if (residualSites.Count == 0)
        {
            return;
        }

        var severity = options.Strict
            ? NumericBoundarySeverity.Error
            : NumericBoundarySeverity.Warning;
        diagnostics.Add(new NumericBoundaryDiagnostic(
            severity,
            PreprocessDiagnosticCodes.NullableLiftResidual,
            NullableLiftDiagnostics.Residual(string.Join(", ", residualSites))));
        var key = PreprocessDiagnosticCodes.NullableLiftResidual;
        stats.DeferredByReason[key] = stats.DeferredByReason.TryGetValue(key, out var count)
            ? count + 1
            : 1;
    }

    /// <summary>
    /// M1 NL-14 ownership predicate: a residual lifted built-in binary/unary
    /// on an M1-owned domain/flavor. Pure over the bound model so it can be
    /// unit-tested directly (§7.1-12).
    /// </summary>
    internal static bool IsM1OwnedResidual(
        NullableLiftAnalyzer analyzer, SyntaxNode node, IOperation operation)
    {
        switch (operation)
        {
            case NBOp.IBinaryOperation binary when binary.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(binary.OperatorMethod)
                && node is BinaryExpressionSyntax binarySyntax:
            {
                var classification = analyzer.ClassifyBinary(binarySyntax);
                if (classification == null
                    || classification.Flavor == LiftedBinaryFlavor.UserDefined
                    || !IsM1OwnedDomain(classification.Domain))
                {
                    return false;
                }

                return classification.Flavor is
                    LiftedBinaryFlavor.Arithmetic
                    or LiftedBinaryFlavor.Relational
                    or LiftedBinaryFlavor.Equality
                    or LiftedBinaryFlavor.BoolAnd
                    or LiftedBinaryFlavor.BoolOr
                    or LiftedBinaryFlavor.BoolXor;
            }

            case NBOp.IUnaryOperation unary when unary.IsLifted
                && NullableLiftAnalyzer.IsBuiltInLiftedOperator(unary.OperatorMethod)
                && node is PrefixUnaryExpressionSyntax unarySyntax:
            {
                var classification = analyzer.ClassifyUnary(unarySyntax);
                if (classification == null
                    || classification.Flavor == LiftedUnaryFlavor.UserDefined
                    || !IsM1OwnedDomain(classification.Domain))
                {
                    return false;
                }

                return classification.Flavor is
                    LiftedUnaryFlavor.Arithmetic
                    or LiftedUnaryFlavor.BoolNot;
            }

            default:
                return false;
        }
    }

    /// <summary>M1 owns numeric and bool domains; Enum/SpecialNumeric/Other are deferred.</summary>
    private static bool IsM1OwnedDomain(NullableValueDomain domain) =>
        domain is NullableValueDomain.Integer
            or NullableValueDomain.Unsigned
            or NullableValueDomain.Floating
            or NullableValueDomain.Decimal
            or NullableValueDomain.Bool;

    /// <summary>CS2J5350/CS2J5351 aggregated, verbose-only Info diagnostics.</summary>
    private static void EmitInfo(
        NullableLiftStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var totalSites = stats.LiftedBinarySites
            + stats.LiftedUnarySites
            + stats.LiftedRelationalSites
            + stats.LiftedEqualitySites
            + stats.LiftedBoolLogicSites;
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

        diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Info,
            PreprocessDiagnosticCodes.NullableLiftSiteInfo,
            NullableLiftDiagnostics.SiteInfo(totalSites, string.Join(", ", breakdown))));

        if (stats.TempsLifted > 0)
        {
            diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Info,
                PreprocessDiagnosticCodes.NullableLiftTempInfo,
                NullableLiftDiagnostics.TempInfo(
                    stats.TempsLifted, "single-evaluation operand hoist")));
        }
    }

    // ── M0 NL-14 full-scope residual scanner (retained for the M0 probes;
    // the live M1 gate is the staged IsM1OwnedResidual predicate above) ──

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
    /// NL-14 full-scope predicate across every M1+M2 lifted shape (M0 probe):
    /// lifted binary/unary/increment/compound with a built-in operator, a
    /// <c>Nullable&lt;T&gt;.GetValueOrDefault</c> call, or a T?→T unwrap /
    /// S?→T? lifted conversion.
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

    /// <summary>Full-scope M0 residual scan (kept for the M0 probe tests).</summary>
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
