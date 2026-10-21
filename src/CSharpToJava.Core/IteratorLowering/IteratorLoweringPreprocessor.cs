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
    /// <summary>
    /// A block is pre-formatted statement-by-statement once it holds at least
    /// this many statements. Small blocks are cheap to format in context and
    /// stay with the whole-tree formatter.
    /// </summary>
    private const int ChunkedBlockMinStatements = 8;

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

        // Dirty pre-filter: `yield return` / `yield break` are the only source
        // forms that make a method an iterator, so a file whose text does not
        // contain the `yield` token cannot hold one. Testing the bytes first
        // skips materializing the syntax root and walking every node of the
        // (often huge) file for the overwhelming majority of clean inputs.
        if (!sourceCode.Contains("yield"))
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
        var formatted = FormatSynthesized(newRoot, workspace);
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

        // Residual gate: count remaining yield statements in output. The
        // formatted tree already carries the exact same statement structure as
        // a re-parse of its text, so the count is read straight off it instead
        // of paying a second full parse of the (potentially multi-megabyte) output.
        var remainingYields = formatted.DescendantNodes()
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

    /// <summary>
    /// Formats the rewritten tree. The synthesized state machines are large,
    /// wholly trivia-less subtrees: a single whole-tree <see cref="Formatter"/>
    /// pass synthesizes whitespace for every trivia-less token and its cost is
    /// super-linear in that token count. Instead, each large fully-synthesized
    /// statement list is formatted in isolation (cheap, small trees) and spliced
    /// back carrying canonical trivia, so the final whole-tree pass only fills
    /// the few remaining gaps and runs in near-linear time. The output text is
    /// byte-for-byte identical to the single whole-tree pass.
    /// </summary>
    private static CompilationUnitSyntax FormatSynthesized(
        CompilationUnitSyntax root, Microsoft.CodeAnalysis.Workspace workspace)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var annotated in root.GetAnnotatedNodes(IteratorStateMachineRewriter.Annotation))
        {
            CollectSynthesizedChunks(annotated, replacements, workspace);
        }

        var prepared = replacements.Count > 0
            ? root.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original])
            : (SyntaxNode)root;
        return (CompilationUnitSyntax)Formatter.Format(prepared, workspace);
    }

    /// <summary>
    /// Walks the annotated (rewritten / synthesized) subtrees looking for the
    /// large flat statement lists the iterator lowering emits; descends no
    /// further once a block is handled, since the statements inside it are
    /// formatted as a unit.
    /// </summary>
    private static void CollectSynthesizedChunks(
        SyntaxNode node,
        Dictionary<SyntaxNode, SyntaxNode> replacements,
        Microsoft.CodeAnalysis.Workspace workspace)
    {
        foreach (var child in node.ChildNodes())
        {
            if (child is BlockSyntax block
                && block.Statements.Count >= ChunkedBlockMinStatements)
            {
                // Only blocks whose every statement is produced by SyntaxFactory
                // (no comment / explicit newline trivia copied from the source)
                // are pre-formatted: their in-context layout is fully derivable
                // from the node shape, so isolated formatting is exact. Blocks
                // that embed preserved original layout are left untouched for
                // the whole-tree formatter to keep their existing line breaks.
                if (block.Statements.All(IsSynthesizedStatement))
                {
                    ReplaceSynthesizedStatements(block, replacements, workspace);
                }
                continue;
            }

            CollectSynthesizedChunks(child, replacements, workspace);
        }
    }

    private static void ReplaceSynthesizedStatements(
        BlockSyntax block,
        Dictionary<SyntaxNode, SyntaxNode> replacements,
        Microsoft.CodeAnalysis.Workspace workspace)
    {
        StatementSyntax? previous = null;
        foreach (var statement in block.Statements)
        {
            // Formatting the statement together with its predecessor in a
            // throw-away block lets the formatter pick the canonical separator
            // (blank line or not) between the two, which is read back off the
            // second statement's leading trivia.
            var probe = previous == null
                ? (SyntaxNode)statement
                : SyntaxFactory.Block(previous, statement);
            var text = Formatter.Format(probe, workspace).ToFullString();

            StatementSyntax? parsed;
            if (previous == null)
            {
                parsed = SyntaxFactory.ParseStatement(text);
                if (parsed is { ContainsDiagnostics: false })
                {
                    var first = parsed.GetFirstToken();
                    parsed = parsed.ReplaceToken(first, first.WithLeadingTrivia());
                }
                else
                {
                    parsed = null;
                }
            }
            else if (SyntaxFactory.ParseStatement(text) is BlockSyntax probeBlock
                && !probeBlock.ContainsDiagnostics
                && probeBlock.Statements.Count >= 2)
            {
                var separated = probeBlock.Statements[1];
                var separator = probeBlock.Statements[0].GetLastToken().TrailingTrivia
                    .AddRange(separated.GetLeadingTrivia());
                var first = separated.GetFirstToken();
                parsed = separated.ReplaceToken(first, first.WithLeadingTrivia(separator));
            }
            else
            {
                parsed = null;
            }

            if (parsed != null)
            {
                // Drop the outer trivia so the enclosing whole-tree formatter
                // supplies the canonical separator on both sides.
                var last = parsed.GetLastToken();
                parsed = parsed.ReplaceToken(last, last.WithTrailingTrivia());
                replacements[statement] = parsed;
            }

            previous = statement;
        }
    }

    /// <summary>
    /// True when every token of the statement carries no comment and no
    /// newline trivia, i.e. the statement is built purely from SyntaxFactory
    /// and the formatter would synthesize all of its whitespace from scratch.
    /// </summary>
    private static bool IsSynthesizedStatement(StatementSyntax statement)
        => !statement.DescendantTokens().Any(t =>
            HasUnsafeTrivia(t.LeadingTrivia) || HasUnsafeTrivia(t.TrailingTrivia));

    private static bool HasUnsafeTrivia(SyntaxTriviaList trivia)
    {
        foreach (var single in trivia)
        {
            if (single.Kind() == SyntaxKind.SingleLineCommentTrivia
                || single.Kind() == SyntaxKind.MultiLineCommentTrivia)
            {
                return true;
            }

            var text = single.ToString();
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
            {
                return true;
            }
        }

        return false;
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

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var getter = node.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (getter?.Body == null || !MethodContainsYield(getter.Body)) return node;

        var rewrittenBody = RewriteIteratorBody(getter.Body);
        return node.WithAccessorList(node.AccessorList!.ReplaceNode(getter, getter.WithBody(rewrittenBody)));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var getter = node.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (getter?.Body == null || !MethodContainsYield(getter.Body)) return node;

        var rewrittenBody = RewriteIteratorBody(getter.Body);
        return node.WithAccessorList(node.AccessorList!.ReplaceNode(getter, getter.WithBody(rewrittenBody)));
    }

    private BlockSyntax RewriteIteratorBody(BlockSyntax body)
    {
        var previous = _inIteratorMethod;
        _inIteratorMethod = true;
        try
        {
            return (BlockSyntax)Visit(body)!;
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
