using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Public entry point for the iterator-lowering preprocessor. Mirrors the
/// EnumPreprocessor pattern: dirty check -> analyze -> rewrite -> format -> residual gate.
/// </summary>
public sealed class IteratorLoweringPreprocessor
{
    public IteratorLoweringFileResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        IteratorLoweringOptions? options = null)
    {
        options ??= new IteratorLoweringOptions();
        var stats = new IteratorLoweringStatistics { FilesScanned = 1 };
        var diagnostics = new List<IteratorLoweringDiagnostic>();

        if (semanticModel == null)
        {
            stats.FilesSkippedClean = 1;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        // Dirty check: no yield statements -> byte-level copy
        if (!root.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
            || n.IsKind(SyntaxKind.YieldBreakStatement)))
        {
            stats.FilesSkippedClean = 1;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        // M3 (C44 G3): normalize using statements/declarations and lock statements
        // inside iterator bodies to try/finally before state machine analysis.
        // The rewrite binds against the original semantic model; afterwards the
        // tree is replaced inside a cloned compilation so Analyzer/Rewriter run
        // against a model whose spans match the normalized syntax.
        var normalizer = new IteratorResourceNormalizer(semanticModel);
        var normalizedRoot = normalizer.Normalize(root);
        var activeModel = semanticModel;
        var normalized = normalizer.Diagnostics.Count > 0
            || normalizer.UsingRegionsNormalized > 0
            || normalizer.LocksNormalized > 0;
        if (normalized)
        {
            foreach (var diag in normalizer.Diagnostics)
            {
                var severity = diag.Severity;
                if (!options.Strict && severity == IteratorLoweringSeverity.Error)
                    severity = IteratorLoweringSeverity.Warning;
                diagnostics.Add(diag with { Severity = severity });
            }

            var normalizedTree = CSharpSyntaxTree.Create(
                normalizedRoot,
                (CSharpParseOptions?)root.SyntaxTree.Options,
                root.SyntaxTree.FilePath,
                System.Text.Encoding.UTF8);
            var replacementCompilation = ((CSharpCompilation)semanticModel.Compilation)
                .ReplaceSyntaxTree(semanticModel.SyntaxTree, normalizedTree);
            activeModel = replacementCompilation.GetSemanticModel(normalizedTree);
            // CSharpSyntaxTree.Create clones the root, so all downstream node
            // identity (Analyzer plans -> Rewriter dictionary keys) must come
            // from the tree's own root, never from the detached normalizedRoot.
            root = normalizedTree.GetCompilationUnitRoot();
        }

        // Analyze
        var analyzerResult = IteratorLoweringAnalyzer.Analyze(activeModel, options);

        // Merge diagnostics with severity adjustment
        foreach (var diag in analyzerResult.Diagnostics)
        {
            var severity = diag.Severity;
            if (!options.Strict && severity == IteratorLoweringSeverity.Error)
                severity = IteratorLoweringSeverity.Warning;
            diagnostics.Add(diag with { Severity = severity });
        }

        // If no plans, nothing to rewrite
        if (analyzerResult.Plans.Count == 0)
        {
            stats.FilesDirtyNoRewrite = 1;
            stats.SkippedUnsupported = analyzerResult.Diagnostics.Count;
            return new IteratorLoweringFileResult(sourceCode, false, diagnostics, stats);
        }

        // Rewrite
        var rewriter = new IteratorStateMachineRewriter(activeModel, analyzerResult);
        var newRoot = rewriter.Rewrite(root);

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        // Merge statistics
        stats.FilesTransformed = 1;
        stats.IteratorMembersLowered = analyzerResult.Plans.Count;
        stats.StateMachinesGenerated = analyzerResult.Plans.Count;
        stats.YieldReturnSites = analyzerResult.Plans.Sum(p => p.YieldReturnCount);
        stats.YieldBreakSites = analyzerResult.Plans.Count(p => p.HasYieldBreak);
        stats.HoistedParameters = analyzerResult.Plans.Sum(p => p.Parameters.Count);
        stats.HoistedLocals = analyzerResult.Plans.Sum(p => p.HoistedLocals.Count);
        stats.HoistedEnumerators = analyzerResult.Plans.Sum(p => p.HoistedEnumerators.Count);
        stats.FinallyRegions = analyzerResult.Plans.Sum(p => p.Regions.Count);
        stats.SkippedUnsupported = analyzerResult.Diagnostics.Count;
        stats.UsingRegionsNormalized = normalizer.UsingRegionsNormalized;
        stats.UsingResourcesHoisted = normalizer.UsingResourcesHoisted;
        stats.LocksNormalized = normalizer.LocksNormalized;

        // Residual gate: count remaining yield statements in output
        var outputRoot = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        var remainingYields = outputRoot.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Count();
        stats.ResidualCount = remainingYields;

        if (remainingYields > 0 && options.Strict)
        {
            diagnostics.Add(new IteratorLoweringDiagnostic(
                IteratorLoweringSeverity.Error,
                IteratorLoweringDiagnosticCode.CS2J0402,
                $"{remainingYields} residual yield statement(s) remaining after preprocessing."));
        }

        return new IteratorLoweringFileResult(code, true, diagnostics, stats);
    }
}

public sealed record IteratorLoweringFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics,
    IteratorLoweringStatistics Statistics);
