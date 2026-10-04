using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional.Assignment;

/// <summary>Diagnostic codes owned by the C51 assignment rule (design §6).</summary>
internal static class NullConditionalAssignmentDiagnosticCodes
{
    /// <summary>NA-6: <c>??=</c> combined with a null-conditional assignment (Info).</summary>
    public const string CS2J0568 = "CS2J0568";

    /// <summary>Safety net: native-illegal/damaged/unsupported assignment shape (Error).</summary>
    public const string CS2J0569 = "CS2J0569";
}

/// <summary>Message templates for the C51 diagnostic codes.</summary>
internal static class NullConditionalAssignmentDiagnostics
{
    public const string AssignmentSiteMessage =
        "Null-conditional assignment site explicitized.";

    public const string CoalesceAssignmentMessage =
        "Null-conditional assignment combined with ??= (NA-6).";

    public const string ValueTypeReceiverMessage =
        "Conditional assignment whose receiver is a value type cannot be " +
        "lowered safely; a record-struct writable site is handed to the " +
        "make-readonly pass.";

    public const string RecordStructHandoffMessage =
        "Record-struct conditional assignment site preserved for the " +
        "make-readonly pass.";

    public const string SafetyNetMessage =
        "Null-conditional assignment cannot be lowered: {0} (the baseline " +
        "compiler rejects this shape or it was produced by a pass without " +
        "the assignment guard).";
}

/// <summary>
/// C51 terminal dispatcher (design §4.1): invoked by the C35 analyzer when
/// the chain terminal is an assignment. Runs the guard/shape decision table
/// and accumulates the assignment statistics; the actual rendering stays in
/// <see cref="NullConditionalAssignmentEmitter"/> and the rewriter bridge.
/// </summary>
internal static class NullConditionalAssignmentRule
{
    /// <summary>
    /// Decides the outcome/shape of one assignment-terminal plan. Mutates the
    /// plan (Lowered per NA-1/2/5, or Preserved with an explanatory code) and
    /// reports diagnostics.
    /// </summary>
    public static void Decide(ChainPlan plan, RuleAnalysisContext context)
    {
        var model = context.SemanticModel!;
        var strict = context.Options.NullConditional!.Strict;
        var stats = context.Statistics.NullConditional;
        var root = plan.RootOriginal;
        var assignment = (AssignmentExpressionSyntax)
            plan.Terminal.ExpressionOriginal;

        // ---- 1. E03-damaged shape anywhere in the file (global) ----
        if (NullConditionalAssignmentFacts.HasOrphanBindings(
                context.Root))
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "orphan placeholder binding from an earlier ??/??= rewrite");
            return;
        }

        // ---- 2. Native-illegal chain shapes: ++/--, deconstruction ----
        if (NullConditionalAssignmentFacts.IsUnaryWrapped(root))
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "increment/decrement (++/--) binds over a null-conditional expression (CS1059)");
            return;
        }

        if (NullConditionalAssignmentFacts.IsDeconstructionLhs(root))
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "deconstruction assignment target contains a null-conditional expression");
            return;
        }

        // ---- 3. ref assignment RHS (a?.b = ref x; CS8373) ----
        if (NullConditionalAssignmentFacts.StripParens(assignment.Right)
            is RefExpressionSyntax)
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "right side is a ref expression (ref assignment)");
            return;
        }

        // ---- 4. Operator / target classification ----
        var operatorKind =
            NullConditionalAssignmentFacts.ClassifyOperator(assignment);
        var targetShape =
            NullConditionalAssignmentFacts.ClassifyTarget(
                assignment, model, out var isStatic);

        // NA-6 lands in the later slice; until then keep the C51 deferral.
        if (operatorKind == AssignmentOperatorKind.Coalesce)
        {
            plan.Outcome = ChainOutcome.Preserved;
            plan.SkipCode = null;
            stats.DeferredForC51Sites++;
            return;
        }

        if (operatorKind == AssignmentOperatorKind.Unknown
            || targetShape == AssignmentTargetShape.Unknown)
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "unrecognized assignment operator or target shape");
            return;
        }

        // An event target only supports compound (+=/-=) access.
        if (targetShape == AssignmentTargetShape.Event
            && operatorKind != AssignmentOperatorKind.Compound)
        {
            Preserve(plan, NullConditionalAssignmentDiagnosticCodes.CS2J0569);
            ReportSafetyNet(context, strict,
                "event target requires += or -=");
            return;
        }

        // ---- 5. Value-type receivers (F9 / Q3) ----
        if (NullConditionalAssignmentFacts.IsValueTypeReceiver(
                plan, out var isNullable, out var recordStruct))
        {
            if (isNullable && recordStruct)
            {
                // CS2J0562 Info: keep the CA, hand off to make-readonly.
                Preserve(plan, NullConditionalDiagnosticCodes.CS2J0562);
                context.Report(
                    ExpressionDesugarSeverity.Info,
                    NullConditionalDiagnosticCodes.CS2J0562,
                    NullConditionalAssignmentDiagnostics.RecordStructHandoffMessage,
                    root);
                stats.AddAssignmentSkip(NullConditionalDiagnosticCodes.CS2J0562);
                return;
            }

            // CS2J0561: plain struct receiver or Nullable<non-record struct>.
            Preserve(plan, NullConditionalDiagnosticCodes.CS2J0561);
            var severity = NullConditionalDiagnostics.ResolveStrictError(strict);
            context.Report(
                severity,
                NullConditionalDiagnosticCodes.CS2J0561,
                NullConditionalAssignmentDiagnostics.ValueTypeReceiverMessage,
                root);
            stats.AddAssignmentSkip(NullConditionalDiagnosticCodes.CS2J0561);
            return;
        }

        // ---- 6. Static member target (M17: baseline CS0176) ----
        if (isStatic)
        {
            //归基线错误：保留节点、仅计数，不产生我们自己的 Error。
            plan.Outcome = ChainOutcome.Preserved;
            plan.SkipCode = null;
            stats.StaticTargetSites++;
            return;
        }

        // ---- 7. Shape selection ----
        var receiverWhitelist = NullConditionalSyntaxFacts.IsInlineWhitelistReceiver(
            root.Expression, model);

        if (plan.ContextKind == NullConditionalContextKind.VoidStatement)
        {
            if (plan.Anchor is not ExpressionStatementSyntax)
            {
                var code = plan.Anchor == null
                    ? NullConditionalDiagnosticCodes.CS2J0557
                    : NullConditionalDiagnosticCodes.CS2J0558;
                Preserve(plan, code);
                context.Report(
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    code,
                    plan.Anchor == null
                        ? string.Format(
                            NullConditionalDiagnostics.UnliftableMessage,
                            root.ToString())
                        : NullConditionalDiagnostics.AnchorlessVoidMessage,
                    root);
                stats.AddAssignmentSkip(code);
                return;
            }

            plan.Outcome = ChainOutcome.Lowered;
            plan.Shape = NullConditionalLoweringShape.StatementIf;
        }
        else if (plan.Length > 1)
        {
            if (plan.Anchor is LocalDeclarationStatementSyntax or ReturnStatementSyntax)
            {
                plan.Outcome = ChainOutcome.Lowered;
                plan.Shape = NullConditionalLoweringShape.GuardedBlock;
            }
            else
            {
                Preserve(plan, NullConditionalDiagnosticCodes.CS2J0557);
                context.Report(
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    NullConditionalDiagnosticCodes.CS2J0557,
                    string.Format(
                        NullConditionalDiagnostics.UnliftableMessage,
                        root.ToString()),
                    root);
                stats.AddAssignmentSkip(NullConditionalDiagnosticCodes.CS2J0557);
                return;
            }
        }
        else
        {
            var arguments = assignment.DescendantNodes().OfType<ArgumentSyntax>();

            if (plan.Anchor == null)
            {
                if (receiverWhitelist
                    && NullConditionalSyntaxFacts.AreArgumentsInlineSafe(
                        arguments, model))
                {
                    plan.Outcome = ChainOutcome.Lowered;
                    plan.Shape = NullConditionalLoweringShape.Inline;
                }
                else
                {
                    Preserve(plan, NullConditionalDiagnosticCodes.CS2J0557);
                    context.Report(
                        NullConditionalDiagnostics.ResolveStrictError(strict),
                        NullConditionalDiagnosticCodes.CS2J0557,
                        string.Format(
                            NullConditionalDiagnostics.UnliftableMessage,
                            root.ToString()),
                        root);
                    stats.AddAssignmentSkip(NullConditionalDiagnosticCodes.CS2J0557);
                    return;
                }
            }
            else
            {
                plan.Outcome = ChainOutcome.Lowered;
                plan.Shape = receiverWhitelist
                    ? NullConditionalLoweringShape.Inline
                    : NullConditionalLoweringShape.TempTernary;
            }
        }

        AccumulateSite(plan, stats, targetShape, operatorKind, receiverWhitelist);

        context.Report(
            ExpressionDesugarSeverity.Info,
            NullConditionalDiagnosticCodes.CS2J0555,
            NullConditionalAssignmentDiagnostics.AssignmentSiteMessage,
            root);
    }

    /// <summary>Accumulates the §5 site buckets for one lowered assignment.</summary>
    private static void AccumulateSite(
        ChainPlan plan,
        NullConditionalStatistics statistics,
        AssignmentTargetShape targetShape,
        AssignmentOperatorKind operatorKind,
        bool receiverWhitelist)
    {
        switch (targetShape)
        {
            case AssignmentTargetShape.Member:
            case AssignmentTargetShape.RefReturningMethod:
                statistics.AssignmentMemberSites++;
                break;

            case AssignmentTargetShape.Index:
                statistics.AssignmentIndexSites++;
                break;

            case AssignmentTargetShape.Event:
                statistics.EventCompoundSites++;
                break;
        }

        var isValue = plan.ContextKind == NullConditionalContextKind.Value;
        if (isValue)
            statistics.ValueFormAssignmentSites++;

        // Receiver temp count (whitelist statement/single inline = zero).
        var receiverTemps = plan.Shape switch
        {
            NullConditionalLoweringShape.Inline => 0,
            NullConditionalLoweringShape.StatementIf
                when plan.Length == 1 && receiverWhitelist => 0,
            _ => plan.Length,
        };
        statistics.AssignmentReceiverTempsLifted += receiverTemps;

        // Chain bucket: M8 multi-segment (M7 ordinary tail is detected in the
        // later slice via the LHS shape).
        if (plan.Length > 1)
            statistics.AssignmentChainSites++;
    }

    private static void Preserve(ChainPlan plan, string code)
    {
        plan.Outcome = ChainOutcome.Preserved;
        plan.SkipCode = code;
    }

    private static void ReportSafetyNet(
        RuleAnalysisContext context,
        bool strict,
        string reason)
    {
        context.Report(
            NullConditionalDiagnostics.ResolveStrictError(strict),
            NullConditionalAssignmentDiagnosticCodes.CS2J0569,
            string.Format(
                NullConditionalAssignmentDiagnostics.SafetyNetMessage, reason),
            null);
        context.Statistics.NullConditional.AddAssignmentSkip(
            NullConditionalAssignmentDiagnosticCodes.CS2J0569);
    }
}
