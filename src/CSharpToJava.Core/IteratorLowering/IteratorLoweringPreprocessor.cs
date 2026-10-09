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

        // M4b: bare `return;` is legal C# inside an iterator block and has the
        // same end-of-enumeration semantics as `yield break` (including finally
        // cleanup). Normalize it to yield break on yielding methods so all
        // downstream stages handle a single statement form. Purely syntactic
        // (no model binding), so it runs after the model-bound resource
        // normalization; nested routines (local functions/lambdas) are not
        // descended into.
        var bareReturnRewriter = new BareReturnToYieldBreakRewriter();
        normalizedRoot = (CompilationUnitSyntax)bareReturnRewriter.Visit(normalizedRoot)!;

        var activeModel = semanticModel;
        var normalized = bareReturnRewriter.Changed
            || normalizer.Diagnostics.Count > 0
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

/// <summary>
/// M4b: rewrites bare <c>return;</c> statements inside yielding method bodies
/// into <c>yield break;</c>. In C# a value-less return inside an iterator block
/// is legal and means "end the enumeration", identical to yield break. Only the
/// enclosing method iterator is rewritten; nested routines (local functions,
/// lambdas, anonymous methods) own their own return statements and are skipped.
/// </summary>
internal sealed class BareReturnToYieldBreakRewriter : CSharpSyntaxRewriter
{
    public bool Changed { get; private set; }

    /// <summary>
    /// true only while traversing the body of a method that itself is an
    /// iterator. Without this gate the default traversal would also reach bare
    /// returns inside constructors/event accessors/finalizers that happen to
    /// live in the same (yield-containing) file and wrongly turn them into
    /// yield break (CS1631).
    /// </summary>
    private bool _inIteratorMethod;

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (node.Body == null || !MethodContainsYield(node.Body))
        {
            // Not an iterator: keep the subtree untouched, which also prevents
            // returns inside nested local functions/lambdas from being touched.
            return node;
        }

        var previous = _inIteratorMethod;
        _inIteratorMethod = true;
        try
        {
            var rewrittenBody = (BlockSyntax)Visit(node.Body)!;
            return node.WithBody(rewrittenBody);
        }
        finally
        {
            _inIteratorMethod = previous;
        }
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (_inIteratorMethod && node.Expression == null)
        {
            Changed = true;
            return SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement);
        }

        return node;
    }

    private static bool MethodContainsYield(BlockSyntax body)
        // Only yields owned by THIS method body qualify (shared prune rule with
        // the analyzer), otherwise a non-iterator outer method that merely
        // contains an iterator local function would get its bare `return;`
        // rewritten to `yield break` (CS1631).
        => IteratorLoweringAnalyzer.MethodOwnsYield(body);
}
