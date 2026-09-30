using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// M1 syntax rewriter for numeric-boundary (rules NB-1/NB-2/NB-6/NB-9/NB-11):
/// strips <c>unchecked(e)</c> shells, unwraps <c>unchecked { … }</c> blocks,
/// materializes compile-time constants, strips <c>checked(e)</c> shells around
/// non-integer operations, and preserves <c>checked(e)</c> shells around
/// user-defined operators for member-desugar (C14).
/// Built-in integer checked sites are left untouched in M1 (NB-3/4/5/7/8/10
/// arrive with M2/M3) so no overflow check can be lost.
/// </summary>
internal sealed class CheckedExprRewriter : CSharpSyntaxRewriter
{
    private readonly NumericBoundaryAnalyzer _analyzer;
    private readonly NumericBoundaryStatistics _stats;

    public CheckedExprRewriter(NumericBoundaryAnalyzer analyzer, NumericBoundaryStatistics stats)
    {
        _analyzer = analyzer;
        _stats = stats;
    }

    /// <summary>True when at least one node was structurally replaced/rewritten.</summary>
    public bool MadeChanges { get; private set; }

    public override SyntaxNode? VisitCheckedExpression(CheckedExpressionSyntax node)
    {
        var isCheckedShell = node.IsKind(SyntaxKind.CheckedExpression);

        ExpressionSyntax visitedInner;
        using (_analyzer.PushContext(isCheckedShell))
        {
            visitedInner = (ExpressionSyntax)Visit(node.Expression)!;
        }

        if (!isCheckedShell)
        {
            // NB-1: unchecked(expr) always comes off (inner was rewritten on the
            // unchecked stack). Constants fold through NB-6 below.
            if (_analyzer.TryMaterializeConstant(node.Expression, out var materialized))
            {
                _stats.ConstantsMaterialized++;
                return FinalizeReplacement(node, materialized);
            }

            _stats.UncheckedExpressionsStripped++;
            return StripShell(node, visitedInner);
        }

        // NB-11 / decision 4: checked user-operator variants (op_Checked*) are
        // method calls; the checked shell is the only syntax context selecting the
        // variant, so it is preserved for member-desugar and counted as an NB-13
        // exemption. (Plain operator variants, including decimal op_Addition, are
        // check-context independent and fall through to the strip path.)
        if (_analyzer.IsCheckedUserOperatorVariant(node.Expression))
        {
            _stats.CheckedUserOpShellsDeferred++;
            return node.WithExpression(visitedInner);
        }

        // NB-6: a constant that compiles inside checked(...) cannot overflow; the
        // folded literal is equivalent and the shell disappears.
        if (_analyzer.TryMaterializeConstant(node.Expression, out var constantLiteral))
        {
            _stats.ConstantsMaterialized++;
            return FinalizeReplacement(node, constantLiteral);
        }

        // M1 boundary: built-in integer arithmetic/conversion keeps its shell.
        if (_analyzer.IsBuiltInIntegerCheckedSite(node.Expression))
        {
            return node.WithExpression(visitedInner);
        }

        // M3/future-code buckets (compound assignment, ++/--, nullable lifted,
        // dynamic): keep the shell now, lose nothing, close later.
        if (_analyzer.IsDeferredCheckedSite(node.Expression))
        {
            return node.WithExpression(visitedInner);
        }

        // NB-9/NB-11: decimal/float/double arithmetic, enum/identity conversions
        // and any other non-checked-sensitive inner form: shell off, op untouched.
        _stats.CheckedNonIntegerShellsStripped++;
        return StripShell(node, visitedInner);
    }

    public override SyntaxNode? VisitCheckedStatement(CheckedStatementSyntax node)
    {
        var isCheckedBlock = node.IsKind(SyntaxKind.CheckedStatement);

        BlockSyntax visitedBlock;
        using (_analyzer.PushContext(isCheckedBlock))
        {
            visitedBlock = (BlockSyntax)Visit(node.Block)!;
        }

        if (isCheckedBlock)
        {
            // NB-10 belongs to M2: M1 keeps the checked block (and thus every
            // overflow check inside) intact.
            return node.WithBlock(visitedBlock);
        }

        // NB-2: unchecked { … } → bare block (keyword gone).
        MadeChanges = true;
        _stats.UncheckedBlocksUnwrapped++;
        var bareBlock = visitedBlock.WithLeadingTrivia(
            node.GetLeadingTrivia().Concat(visitedBlock.GetLeadingTrivia()));

        if (MustKeepBraces(node, visitedBlock))
        {
            // Single-statement context, a labeled parent, or direct local
            // declarations: braces stay to preserve grammar and variable scope.
            return bareBlock;
        }

        // List context without escaping locals: splice statements into the
        // enclosing list (consumed by VisitBlock/VisitSwitchSection below).
        return bareBlock.WithAdditionalAnnotations(NumericBoundarySyntaxFacts.UnwrapBlockAnnotation);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            var visited = Visit(statement);
            if (visited is not StatementSyntax visitedStatement)
            {
                continue;
            }

            if (visitedStatement is BlockSyntax spliced
                && spliced.HasAnnotations(NumericBoundarySyntaxFacts.UnwrapBlockAnnotationKind))
            {
                var list = spliced.Statements.ToList();
                if (list.Count > 0)
                {
                    list[0] = list[0].WithLeadingTrivia(
                        spliced.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia()));
                    list[^1] = list[^1].WithTrailingTrivia(
                        list[^1].GetTrailingTrivia().Concat(spliced.GetTrailingTrivia()));
                }

                foreach (var splicedStatement in list)
                {
                    yield return splicedStatement;
                }

                continue;
            }

            yield return visitedStatement;
        }
    }

    /// <summary>
    /// NB-2 brace retention: unwrapping is only legal in a statement-list context
    /// (block/switch-section) when no declared local escapes the block scope.
    /// </summary>
    private static bool MustKeepBraces(CheckedStatementSyntax node, BlockSyntax block)
    {
        if (block.Statements.Any(s => s.IsKind(SyntaxKind.LocalDeclarationStatement)))
        {
            return true;
        }

        return node.Parent switch
        {
            BlockSyntax => false,
            SwitchSectionSyntax => false,
            _ => true,
        };
    }

    private ExpressionSyntax StripShell(CheckedExpressionSyntax node, ExpressionSyntax visitedInner)
    {
        MadeChanges = true;
        return NumericBoundarySyntaxFacts
            .ParenthesizeIfNeeded(visitedInner)
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(NumericBoundarySyntaxFacts.RewriteAnnotation);
    }

    private ExpressionSyntax FinalizeReplacement(CheckedExpressionSyntax node, ExpressionSyntax replacement)
    {
        MadeChanges = true;
        return replacement
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(NumericBoundarySyntaxFacts.RewriteAnnotation);
    }
}
