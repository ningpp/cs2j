using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// C15 checked/unchecked 规则（原 NumericBoundaryPreprocessor 内硬编码段的薄包装，
/// C17 设计 §4.5 2026-10-01 实施校订，Order=20）：内部 rewriter 一行不改，
/// 本类只负责 Visit/ApplyHoists 与 NB-13 残余闸门调用。
/// </summary>
internal sealed class CheckedArithmeticRule : INumericBoundaryRule
{
    public string Id => "checked-arithmetic";

    public int Order => 20;

    // C15 是族锚点规则：无独立 kill-switch，pass 开启即执行。
    public bool IsEnabled(NumericBoundaryOptions options) => true;

    public NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context)
    {
        var analyzer = context.GetSharedAnalyzer();
        var rewriter = new CheckedExprRewriter(
            analyzer, context.Stats, context.Diagnostics, context.Options.EnableDecimalOps);
        var visitedRoot = (CompilationUnitSyntax)rewriter.Visit(context.Root)!;
        var newRoot = (CompilationUnitSyntax)rewriter.ApplyHoists(visitedRoot);

        // NB-13 residual gate on the rewritten tree (M2/M3: both checked and
        // unchecked keyword nodes must be gone from user code).
        ApplyResidualGate(newRoot, context.Options, context.Stats, context.Diagnostics);

        return new NumericBoundaryRuleResult(newRoot, rewriter.MadeChanges);
    }

    /// <summary>
    /// NB-13: scan the output tree for surviving checked/unchecked nodes.
    /// Unchecked survivors are always an error. Surviving checked EXPRESSION
    /// shells are exactly the user-defined <c>op_Checked*</c> vouchers kept for
    /// member-desugar (their count is tracked during the traversal); they are
    /// exempt only while member-desugar co-runs (decision 4 / open question 6).
    /// Any mismatch (or a surviving checked BLOCK) is CS2J5327 (strict → Error).
    /// </summary>
    private static void ApplyResidualGate(
        SyntaxNode root,
        NumericBoundaryOptions options,
        NumericBoundaryStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var memberDesugarCoRunning =
            options.EnabledPassIds?.Contains(NumericBoundaryOptions.MemberDesugarPassId) == true;

        var residualUnchecked = 0;
        var residualCheckedExpressions = 0;
        var residualCheckedBlocks = 0;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case CheckedExpressionSyntax expression:
                    if (expression.IsKind(SyntaxKind.UncheckedExpression))
                    {
                        residualUnchecked++;
                    }
                    else
                    {
                        residualCheckedExpressions++;
                    }

                    break;
                case CheckedStatementSyntax statement:
                    if (statement.IsKind(SyntaxKind.UncheckedStatement))
                    {
                        residualUnchecked++;
                    }
                    else
                    {
                        residualCheckedBlocks++;
                    }

                    break;
            }
        }

        stats.ResidualUncheckedNodes = residualUnchecked;
        stats.ResidualCheckedNodes = residualCheckedExpressions + residualCheckedBlocks;

        // Every surviving checked expression must be one kept voucher.
        var unexpectedChecked = residualCheckedBlocks
            + Math.Max(0, residualCheckedExpressions - stats.CheckedUserOpShellsDeferred);
        var voucherChecked = Math.Min(residualCheckedExpressions, stats.CheckedUserOpShellsDeferred);

        void AddResidual(int count, string message)
        {
            if (count == 0)
            {
                return;
            }

            var severity = options.Strict
                ? NumericBoundarySeverity.Error
                : NumericBoundarySeverity.Warning;
            diagnostics.Add(new NumericBoundaryDiagnostic(
                severity, PreprocessDiagnosticCodes.NumericBoundaryResidualNode, message));
            if (severity == NumericBoundarySeverity.Error)
            {
                stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryResidualNode] =
                    stats.ErrorsByReason.GetValueOrDefault(PreprocessDiagnosticCodes.NumericBoundaryResidualNode) + count;
            }
        }

        if (residualUnchecked > 0)
        {
            AddResidual(
                residualUnchecked,
                $"{residualUnchecked} residual unchecked node(s) remain after numeric-boundary " +
                "preprocessing; user code must not keep unchecked expressions/blocks.");
        }

        if (voucherChecked > 0 && !memberDesugarCoRunning)
        {
            AddResidual(
                voucherChecked,
                $"{voucherChecked} checked expression shell(s) wrapping user-defined checked operators " +
                "(op_Checked*) remain, but the member-desugar pass is not enabled in this chain; " +
                "enable member-desugar alongside numeric-boundary.");
        }

        if (unexpectedChecked > 0)
        {
            AddResidual(
                unexpectedChecked,
                $"{unexpectedChecked} residual checked node(s) remain after numeric-boundary preprocessing; " +
                "user code must not keep checked expressions/blocks.");
        }
    }
}
