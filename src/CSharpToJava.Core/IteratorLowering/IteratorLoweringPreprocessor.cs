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
    /// A statement list is pre-formatted once it holds at least this many
    /// statements. Smaller lists stay with the whole-tree formatter: isolated
    /// formatting of tiny nested lists (state-machine case bodies) is NOT
    /// byte-equivalent (verified by byte-diff), and in-context formatting of
    /// small lists is cheap.
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

        // Giant yield-only iterators (hundreds of plain `yield return expr;`
        // statements) lower to one state-machine MoveNext whose bytecode busts
        // the JVM's 64KB per-method limit. Split them into chunk iterators
        // forwarded by the original method BEFORE analysis; purely syntactic.
        var giantIteratorSplitter = new GiantYieldOnlyIteratorSplitter();
        normalizedRoot = (CompilationUnitSyntax)giantIteratorSplitter.Visit(normalizedRoot)!;

        var activeModel = semanticModel;
        var normalized = bareReturnRewriter.Changed
            || giantIteratorSplitter.Changed
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
        var remainingYieldNodes = formatted.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .ToList();
        var remainingYields = remainingYieldNodes.Count;
        stats.ResidualCount = remainingYields;

        if (remainingYields > 0 && options.Strict)
        {
            var firstSpan = remainingYieldNodes[0].GetLocation().GetLineSpan().StartLinePosition;
            diagnostics.Add(new IteratorLoweringDiagnostic(
                IteratorLoweringSeverity.Error,
                IteratorLoweringDiagnosticCode.CS2J0402,
                $"{remainingYields} residual yield statement(s) remaining after preprocessing " +
                $"(first at line {firstSpan.Line + 1}, column {firstSpan.Character + 1})."));
        }

        return new IteratorLoweringFileResult(code, true, diagnostics, stats, formatted);
    }

    /// <summary>
    /// Formats a rewritten tree whose synthesized subtrees are wholly
    /// trivia-less: each large fully-synthesized statement list is formatted
    /// in isolation (cheap, small trees, in parallel across blocks) and
    /// spliced back carrying canonical trivia, so the final whole-tree pass
    /// only fills the few remaining gaps and runs in near-linear time. The
    /// output text is byte-for-byte identical to the single whole-tree pass
    /// (the isolated format of an all-synthesized block is exact). Shared
    /// with the goto eliminator, whose state machines have the same shape.
    /// </summary>
    internal static CompilationUnitSyntax FormatSynthesized(
        CompilationUnitSyntax root, Microsoft.CodeAnalysis.Workspace workspace)
    {
        // Collect the qualifying statement lists first, then pre-format them
        // in parallel: lists never nest (the walk stops at a handled one) and
        // replacements are statement-level, so per-list results are disjoint
        // and order-independent — the merged replacement map, and therefore
        // the formatted tree, is identical to the sequential pass. Both flat
        // blocks AND switch sections qualify: the state machines emit their
        // case bodies as section statement lists, which the whole-tree
        // formatter handles super-linearly when left trivia-less.
        var lists = new List<IReadOnlyList<StatementSyntax>>();
        CollectSynthesizedStatementLists(root, lists);

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        if (lists.Count > 0)
        {
            var merged = new object();
            System.Threading.Tasks.Parallel.ForEach(lists, statements =>
            {
                var local = new Dictionary<SyntaxNode, SyntaxNode>();
                ReplaceSynthesizedStatements(statements, local, new AdhocWorkspace());
                lock (merged)
                {
                    foreach (var pair in local)
                    {
                        replacements[pair.Key] = pair.Value;
                    }
                }
            });
        }
        var prepared = replacements.Count > 0
            ? root.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original])
            : (SyntaxNode)root;
        // Whole-tree pass (span-limited formatting of the annotated regions is
        // NOT equivalent: the formatter also normalizes untouched source
        // regions — verified by byte-diff against the baseline tree).
        return (CompilationUnitSyntax)Formatter.Format(prepared, workspace);
    }

    /// <summary>A child node is a qualifying list when it is a flat block or
    /// switch-section statement list of fully-synthesized statements.</summary>
    private static bool TryGetQualifyingList(SyntaxNode child, out IReadOnlyList<StatementSyntax> statements)
    {
        statements = child switch
        {
            BlockSyntax block when block.Statements.Count >= ChunkedBlockMinStatements => block.Statements,
            SwitchSectionSyntax section when section.Statements.Count >= ChunkedBlockMinStatements => section.Statements,
            _ => [],
        };

        return statements is { Count: > 0 } && statements.All(IsSynthesizedStatement);
    }

    /// <summary>
    /// Walks the tree looking for the large flat synthesized statement lists
    /// the rewriters emit — method-body blocks and switch-section bodies;
    /// descends no further once a list is handled, since the statements inside
    /// it are formatted as a unit.
    /// </summary>
    private static void CollectSynthesizedStatementLists(
        SyntaxNode node,
        List<IReadOnlyList<StatementSyntax>> lists)
    {
        foreach (var child in node.ChildNodes())
        {
            // Only lists whose every statement is produced by SyntaxFactory
            // (no comment / explicit newline trivia copied from the source)
            // are pre-formatted: their in-context layout is fully derivable
            // from the node shape, so isolated formatting is exact. Lists
            // that embed preserved original layout are left untouched for
            // the whole-tree formatter to keep their existing line breaks.
            if (TryGetQualifyingList(child, out var statements))
            {
                lists.Add(statements);
                continue;
            }

            CollectSynthesizedStatementLists(child, lists);
        }
    }

    private static void ReplaceSynthesizedStatements(
        IReadOnlyList<StatementSyntax> statements,
        Dictionary<SyntaxNode, SyntaxNode> replacements,
        Microsoft.CodeAnalysis.Workspace workspace)
    {
        // Each statement's probe pairs it with the PREVIOUS ORIGINAL statement
        // (not the previous output), so every iteration is independent and the
        // loop fans out; results are per-statement replacements whose merge
        // order is irrelevant. The probe inputs and the extracted trivia are
        // exactly those of the sequential pass.
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            // Formatting the statement together with its predecessor in a
            // throw-away block lets the formatter pick the canonical separator
            // (blank line or not) between the two, which is read back off the
            // second statement's leading trivia.
            var probe = i == 0
                ? (SyntaxNode)statement
                : SyntaxFactory.Block(statements[i - 1], statement);
            var text = Formatter.Format(probe, new AdhocWorkspace()).ToFullString();

            StatementSyntax? parsed;
            if (i == 0)
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
    IteratorLoweringStatistics Statistics,
    // Formatted output tree (null when unchanged). Lets an in-process caller
    // chain the goto eliminator without re-parsing the output text; a
    // re-parse of OutputCode produces an equivalent tree.
    CompilationUnitSyntax? FormattedRoot = null);

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

/// <summary>
/// Splits static parameterless iterator methods whose body is nothing but a
/// long run of plain <c>yield return expr;</c> statements into several chunk
/// iterator methods, with the original method forwarding via
/// <c>foreach (var item in ChunkK()) yield return item;</c>. Without the split
/// the lowered state machine's single MoveNext exceeds the JVM 64KB per-method
/// bytecode limit ("code too large"). Purely syntactic; no semantic binding.
/// </summary>
public sealed class GiantYieldOnlyIteratorSplitter : CSharpSyntaxRewriter
{
    private const int SplitThreshold = 500;
    private const int ChunkSize = 250;
    private const string LoopItemName = "__cs2jYieldChunkItem";

    public bool Changed { get; private set; }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        return SplitGiantIterators(visited);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        return SplitGiantIterators(visited);
    }

    private TypeDeclarationSyntax SplitGiantIterators(TypeDeclarationSyntax visited)
    {
        var members = new List<MemberDeclarationSyntax>(visited.Members.Count);
        var modified = false;
        foreach (var member in visited.Members)
        {
            if (member is MethodDeclarationSyntax method
                && TrySplit(method, out var forwarding, out var chunks))
            {
                members.Add(forwarding);
                members.AddRange(chunks);
                modified = true;
            }
            else
            {
                members.Add(member);
            }
        }

        if (!modified)
            return visited;
        Changed = true;
        return visited.WithMembers(SyntaxFactory.List(members));
    }

    private static bool TrySplit(
        MethodDeclarationSyntax method,
        out MethodDeclarationSyntax forwarding,
        out List<MethodDeclarationSyntax> chunks)
    {
        forwarding = method;
        chunks = new List<MethodDeclarationSyntax>();

        if (method.Body == null
            || method.ExpressionBody != null
            || method.ParameterList.Parameters.Count != 0
            || !method.Modifiers.Any(SyntaxKind.StaticKeyword)
            || method.Body.Statements.Count < SplitThreshold)
        {
            return false;
        }

        // Only a body made exclusively of `yield return expr;` statements can be
        // chunked safely (no control flow, no locals, no cleanup regions).
        foreach (var statement in method.Body.Statements)
        {
            if (!statement.IsKind(SyntaxKind.YieldReturnStatement))
                return false;
        }

        var returnType = method.ReturnType.WithoutTrivia();
        if (!returnType.ToString().Contains("IEnumerable", StringComparison.Ordinal))
            return false;

        var bodyStatements = method.Body.Statements;
        forwarding = method.WithBody(SyntaxFactory.Block(BuildForwardingStatements(
            method.Identifier.ValueText, bodyStatements.Count)));

        var returnTypeText = returnType.ToString();
        var chunkIndex = 0;
        for (var start = 0; start < bodyStatements.Count; start += ChunkSize)
        {
            var count = Math.Min(ChunkSize, bodyStatements.Count - start);
            var chunkBody = SyntaxFactory.Block(bodyStatements.Skip(start).Take(count));
            // Build the header from text: factory-assembled nodes render without
            // separator whitespace and fuse adjacent identifiers into one token.
            var header = $"{method.Modifiers.ToString()} {returnTypeText} {ChunkMethodName(method.Identifier.ValueText, chunkIndex)}()";
            var chunkMethod = SyntaxFactory.ParseMemberDeclaration(header + "\n{ }");
            if (chunkMethod == null)
                return false;
            chunks.Add(((MethodDeclarationSyntax)chunkMethod).WithBody(chunkBody));
            chunkIndex++;
        }

        return true;
    }

    private static IEnumerable<StatementSyntax> BuildForwardingStatements(string sourceName, int totalYields)
    {
        var chunkCount = (totalYields + ChunkSize - 1) / ChunkSize;
        for (var i = 0; i < chunkCount; i++)
        {
            yield return SyntaxFactory.ParseStatement(
                $"foreach (var {LoopItemName} in {ChunkMethodName(sourceName, i)}()) yield return {LoopItemName};");
        }
    }

    private static string ChunkMethodName(string sourceName, int index)
        => $"{sourceName}__cs2jYieldChunk{index}";
}
