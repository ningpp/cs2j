using System.Text;
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.GotoEliminator;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C26 linq-channel pass. Drives the two-phase pipeline from
/// design §4.5 itself:
/// <list type="number">
/// <item>stage 1 — whole-tree query-expression desugaring
/// (<see cref="LinqChannelRewriter.DesugarQueries"/>, pure syntax; files are
/// processed in parallel over an OrdinalIgnoreCase-sorted list for
/// deterministic output);</item>
/// <item>stage 2 — one recompilation over the on-disk stage-1 tree, then a
/// route plan + the stock <c>LinqRewriter</c> with the M3 permanently-
/// intercepting gate attached (<see cref="LinqChannelRewriter.ProcessFile"/>).</item>
/// </list>
/// M3 gate semantics: Queryable/Expression/Mixed/Ambiguous chains and every
/// node inside quoted Expression&lt;&gt; lambdas are preserved; only
/// Enumerable chains outside quoted regions expand as E16. A
/// <see cref="LinqRouteInvariantException"/> (LC0011) means the gate lost a
/// node: the affected file is rolled back to its ORIGINAL source bytes in the
/// intermediate tree, an Error diagnostic is recorded and the pass fails
/// (design §4.3/§6).
/// The pass verifies build/tests on the intermediate tree and deletes the
/// build outputs afterwards; the schemaVersion 2 report is always written to
/// the intermediate root as <c>cs2j-linq-channel-report.json</c>.
/// </summary>
internal sealed class LinqChannelPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-linq-channel-src";
    public const string ReportFileName = "cs2j-linq-channel-report.json";

    private readonly LinqChannelOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly LinqChannelStatistics _statistics = new();
    private readonly List<LinqChannelDiagnostic> _diagnostics = new();
    private IReadOnlyList<MetadataReference> _enhancedReferences = Array.Empty<MetadataReference>();
    private int _desugaredQueryCount;

    /// <summary>
    /// Test-only fault-injection seam (ProjectTests LC0011 rollback E2E): when
    /// set, receives a project-relative path before stage 2 processes it; a
    /// returned exception is thrown as if the gate tripwire had fired. Never
    /// populated by production code paths.
    /// </summary>
    internal static Func<string, LinqRouteInvariantException?>? Lc0011FaultInjector;

    public LinqChannelPassAdapter(
        LinqChannelOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassStubCatalog.Ids.LinqChannel);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    public LinqChannelStatistics Statistics => _statistics;
    public IReadOnlyList<LinqChannelDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;

        // The merged whole-tree binding compilation must see the restored NuGet
        // compile assemblies, exactly like the sibling extension-methods pass
        // (M3 ruling 2): the analyzed tree is the union of every project, but the
        // default reference set is TPA-only, so metadata-only symbols (xunit's
        // Assert, any package type exposing a LINQ-operator-shaped member name)
        // stay unbound and their chains are falsely reported ambiguous (LC0010).
        // The pass' own verification restore runs at the END of ExecuteAsync, so
        // refresh the assets here first; failures are left to that final
        // (reporting) restore so the diagnostics are not duplicated.
        // EnsureEnhancedReferences(tree); // NEGATIVE-CHECK (temporary)

        // Only .cs files participate; non-.cs project files were already copied
        // by CopyProjectFilesAsync during tree preparation, but intermediate-tree
        // input with an empty project graph can skip that copy — carry the
        // assets ourselves (java-keywords precedent). .sln stays handled by
        // tree preparation.
        var pairs = tree.CsFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Relative: Path.GetRelativePath(sourceRoot, file),
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetDest = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, asset));
            Directory.CreateDirectory(Path.GetDirectoryName(assetDest)!);
            tree.ResetScratchFile(asset, assetDest);
        }

        // In unified in-place chains stage 1 rewrites files directly; prime the
        // pristine snapshot before any write so the LC0011 rollback restores the
        // true pre-pass bytes (stage 1 is first-contact for unchanged files only).
        foreach (var pair in pairs)
        {
            tree.Snapshot.Capture(pair.Source);
        }

        var parseOptions = CreateParseOptions(tree);
        var stage1Changed = new bool[pairs.Count];
        var stage1Desugared = new int[pairs.Count];

        // ---- stage 1: whole-tree query desugaring (pure syntax) ----
        Parallel.For(0, pairs.Count, i =>
        {
            var (source, _, dest) = pairs[i];
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var originalText = File.ReadAllText(source);
            var originalRoot = CSharpSyntaxTree.ParseText(
                originalText, parseOptions, path: dest).GetRoot();
            var (desugaredRoot, desugaredCount) = LinqChannelRewriter.DesugarQueries(originalRoot);
            stage1Desugared[i] = desugaredCount;
            stage1Changed[i] = !desugaredRoot.IsEquivalentTo(originalRoot);

            if (stage1Changed[i])
            {
                File.WriteAllText(
                    dest,
                    desugaredRoot.ToFullString(),
                    new UTF8Encoding(FileStartsWithBom(source)));
            }
            else
            {
                tree.ResetScratchFile(source, dest);
            }
        });

        _desugaredQueryCount = stage1Desugared.Sum();

        // ---- stage 2: recompile the desugared tree, route + rewrite per file ----
        var compilation = BuildIntermediateCompilation(tree, pairs);
        var channel = new LinqChannelRewriter(_options);
        var success = true;

        // Stage-2 inputs retained for the §7.10 AfterFiles residual scan:
        // (relative path, stage-2 semantic model, post-stage-1 syntax root).
        var processedInputs = new List<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)>();
        var rolledBackFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pairs.Count; i++)
        {
            var (source, relative, dest) = pairs[i];
            var syntaxTree = compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, dest, StringComparison.OrdinalIgnoreCase));
            if (syntaxTree == null)
            {
                continue;
            }

            var desugaredRoot = await syntaxTree.GetRootAsync();

            // R9 fast-skip: files with neither a LINQ-operator invocation nor a
            // foreach cannot be touched by either LinqRewriter entry point, so
            // skip semantic planning/rewrite for them. (plan.HasLinqChains is
            // the semantic signal used inside ProcessFile; foreach stays in the
            // gate because the foreach-rewrap path can rewrite plain
            // List/array collections.)
            if (!MayBeRewritten(desugaredRoot))
            {
                continue;
            }

            var reportPath = relative.Replace('\\', '/');
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            processedInputs.Add((relative, dest, semanticModel, desugaredRoot));
            LinqChannelRewriteResult result;
            try
            {
                var injected = Lc0011FaultInjector?.Invoke(reportPath);
                if (injected is not null)
                {
                    throw injected;
                }

                result = channel.ProcessFile(
                    semanticModel,
                    desugaredRoot,
                    reportPath);
            }
            catch (LinqRouteInvariantException ex)
            {
                // LC0011 (design §4.3/§6): the three-way gate lost a node and
                // a preserved route reached the expansion branch / fabricated
                // syntax-name fallback. Roll the file back to its ORIGINAL
                // source bytes (discarding even stage-1 desugaring for it) so
                // the intermediate tree stays compilable, record the Error and
                // fail the pass; other files are still processed in this run.
                tree.ResetScratchFile(source, dest);
                lock (_diagnostics)
                {
                    _diagnostics.Add(ex.ToDiagnostic());
                }

                rolledBackFiles.Add(relative);
                success = false;
                if (_verbose)
                {
                    Console.WriteLine($"[linq-channel] LC0011 rollback: {relative}");
                }

                continue;
            }

            _statistics.MergeFrom(result.Statistics);
            lock (_diagnostics)
            {
                _diagnostics.AddRange(result.Diagnostics);
            }

            if (!result.SyntacticallyIdempotent)
            {
                // M1 self-check (design §4.1③): a second desugar pass would
                // still lower query syntax in the output. LC0040; strict
                // upgrades it to an Error.
                _diagnostics.Add(new LinqChannelDiagnostic(
                    _options.Strict
                        ? LinqChannelDiagnosticSeverity.Error
                        : LinqChannelDiagnosticSeverity.Warning,
                    LinqChannelDiagnosticCodes.ResidualMismatch,
                    $"Stage-2 output still contains {result.SecondPassDesugaredCount} query " +
                    "expression(s) a second desugar pass would lower (syntactic idempotency).",
                    reportPath,
                    null));
            }

            if (result.RewriteChanged)
            {
                var fileContext = new PassFileContext
                {
                    SourceCode = await File.ReadAllTextAsync(source),
                    SourceFilePath = source,
                    RelativePath = relative,
                    DestinationFilePath = dest,
                };
                await ProjectPassRunner.WriteRewrittenAsync(
                    tree,
                    fileContext,
                    new PassFileRewrite
                    {
                        OutputCode = result.RewrittenRoot.ToFullString(),
                        Changed = true,
                        PreserveBom = true,
                    });
            }

            if (_verbose)
            {
                Console.WriteLine(
                    $"[linq-channel] {(result.RewriteChanged || stage1Changed[i] ? "transformed" : "clean (copy)")}: {relative}");
            }
        }

        // ---- §7.10 AfterFiles residual gates (whole-tree recompile) ----
        // Files rolled back to their ORIGINAL source by LC0011 are excluded
        // (they intentionally never went through stage 1/stage 2).
        if (processedInputs.Count > 0 && rolledBackFiles.Count < processedInputs.Count)
        {
            var outputCompilation = BuildIntermediateCompilation(tree, pairs);
            foreach (var (relative, dest, inputModel, inputRoot) in processedInputs)
            {
                if (rolledBackFiles.Contains(relative))
                {
                    continue;
                }

                var outputTree = outputCompilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, dest, StringComparison.OrdinalIgnoreCase));
                if (outputTree is null)
                {
                    continue;
                }

                var outputRoot = await outputTree.GetRootAsync();
                var outputModel = outputCompilation.GetSemanticModel(outputTree);
                var reportPath = relative.Replace('\\', '/');

                // Gates ① + ⑤: rerunning stage 2 over the materialized output
                // must move nothing — any in-support-matrix Enumerable chain
                // left behind WOULD move, so a zero-diff second pass is the
                // residual=0 and idempotency proof.
                var secondPass = channel.ProcessFile(outputModel, outputRoot, reportPath);
                if (secondPass.RewriteChanged
                    || secondPass.SecondPassDesugaredCount != 0
                    || !string.Equals(
                        outputRoot.ToFullString(),
                        secondPass.RewrittenRoot.ToFullString(),
                        StringComparison.Ordinal))
                {
                    _diagnostics.Add(new LinqChannelDiagnostic(
                        _options.Strict
                            ? LinqChannelDiagnosticSeverity.Error
                            : LinqChannelDiagnosticSeverity.Warning,
                        LinqChannelDiagnosticCodes.ResidualMismatch,
                        "Residual idempotency mismatch (LC0040): a second channel pass over the "
                        + "materialized output moves the tree (rewriteChanged="
                        + secondPass.RewriteChanged + ", secondPassDesugared="
                        + secondPass.SecondPassDesugaredCount
                        + ") — an in-support-matrix Enumerable chain was left behind or the "
                        + "output is not a fixed point (design §7.10①⑤).",
                        reportPath,
                        null));
                }

                // Gates ②③④: preserved-call stream identity + helper placement.
                var findings = LinqChannelResidualScanner.ScanFile(
                    inputModel,
                    inputRoot,
                    outputModel,
                    outputRoot,
                    reportPath,
                    _options.Strict);
                if (findings.Count > 0)
                {
                    _statistics.ResidualFindings += findings.Count;
                    lock (_diagnostics)
                    {
                        _diagnostics.AddRange(findings);
                    }
                }
            }
        }

        // The stock procedural LINQ rewriter emits iterator methods
        // (`..._ProceduralLinqN`) with `yield return` statements. The
        // iterator-lowering pass has already run earlier in the chain (it must
        // precede eliminate-goto), so those freshly generated iterators would
        // otherwise reach the Java converter as residual yield. Lower them on
        // the materialized stage-2 output before the verification step.
        if (processedInputs.Count > 0)
        {
            // The procedural rewriter also emits lambdas (e.g. a projection
            // nested inside a yielding foreach). Lift those closure shapes the
            // same way the earlier closure-lift pass would have, then rebuild
            // the semantic picture for the state-machine analysis.
            LiftGeneratedClosures(tree, pairs, processedInputs, rolledBackFiles);
            var loweringCompilation = BuildIntermediateCompilation(tree, pairs);
            LowerGeneratedIterators(loweringCompilation, processedInputs, rolledBackFiles);
        }

        // The stock rewriter sees post-desugar trees, so its
        // DesugaredQueryCount does not include stage 1; fold the stage-1
        // tally into the report counters here.
        _statistics.Rewrite.DesugaredQueryCount += _desugaredQueryCount;

        // ---- report (always; design §4.6/§5.2) ----
        var reportJson = LinqChannelReport.From(_statistics).ToJson();
        File.WriteAllText(
            Path.Combine(intermediateRoot, ReportFileName),
            reportJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!string.IsNullOrWhiteSpace(_options.ReportPath))
        {
            var reportCopy = Path.GetFullPath(_options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportCopy)!);
            File.WriteAllText(
                reportCopy,
                reportJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // ---- restore + verify + cleanup ----
        var restoreExitCode = context.Restore();
        if (restoreExitCode == 0)
        {
            var verified = await context.VerifyBuildAndTestsAsync();
            if (!verified)
            {
                success = false;
            }
        }
        else
        {
            success = false;
        }

        ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
        // Strict LC0010/LC0012/LC0040 errors make the pass fail even when the
        // build verification itself succeeded (ProjectPassRunner also ANDs in
        // HasErrors; this keeps context.Success consistent on its own).
        context.Success = success && !HasErrors;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("LinqChannelPassAdapter drives the two-phase file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new LinqChannelDiagnostic(
            LinqChannelDiagnosticSeverity.Error,
            LinqChannelDiagnosticCodes.VerifyRestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new LinqChannelDiagnostic(
            LinqChannelDiagnosticSeverity.Error,
            LinqChannelDiagnosticCodes.VerifyBuildFailed,
            $"dotnet build failed with exit code {exitCode} for the linq-channel intermediate tree."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new LinqChannelDiagnostic(
            LinqChannelDiagnosticSeverity.Error,
            LinqChannelDiagnosticCodes.VerifyTestsFailed,
            $"dotnet test failed with exit code {exitCode} for the linq-channel intermediate tree."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == LinqChannelDiagnosticSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"linq-channel: {_statistics.EnumerableChainsExpanded} chains expanded " +
           $"({_statistics.Rewrite.RewrittenMethodCount} methods), " +
           $"{_statistics.QueryableChainsPreserved} queryable chains preserved, " +
           $"{_statistics.ExpressionArgumentSites} expression-arg sites, " +
           $"{_statistics.GroupByChainsPreserved} groupby chains preserved, " +
           $"{_statistics.AmbiguousChains} ambiguous, " +
           $"{_statistics.Rewrite.SkippedChains.Count} skipped, " +
           $"{_desugaredQueryCount} queries desugared";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != LinqChannelDiagnosticSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    /// <summary>
    /// Lifts lambdas emitted by the procedural LINQ rewriter in files that
    /// still contain `yield`. Those files were closure-lifted before this pass
    /// ran, so the generated shapes have not been through that stage.
    /// </summary>
    private void LiftGeneratedClosures(
        PassTreeContext tree,
        IReadOnlyList<(string Source, string Relative, string Dest)> pairs,
        IReadOnlyList<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)> processedInputs,
        IReadOnlySet<string> rolledBackFiles)
    {
        var compilation = BuildIntermediateCompilation(tree, pairs);
        var preprocessor = new ClosureLiftPreprocessor();

        foreach (var (relative, dest, _, _) in processedInputs)
        {
            if (rolledBackFiles.Contains(relative)) continue;

            var text = File.ReadAllText(dest);
            if (!text.Contains("yield", StringComparison.Ordinal)) continue;

            var treeNode = compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, dest, StringComparison.OrdinalIgnoreCase));
            if (treeNode == null) continue;

            var result = preprocessor.Preprocess(
                text, compilation.GetSemanticModel(treeNode),
                new ClosureLiftOptions(Strict: false));
            if (!result.Changed) continue;

            File.WriteAllText(dest, result.OutputCode, new UTF8Encoding(FileStartsWithBom(dest)));
        }
    }

    /// <summary>
    /// Runs the iterator-lowering preprocessor over stage-2 files that still
    /// contain `yield` (the procedural LINQ rewriter's generated iterators).
    /// Original yields were handled by the earlier iterator-lowering pass;
    /// only output written by this pass can reach here.
    /// </summary>
    private void LowerGeneratedIterators(
        CSharpCompilation compilation,
        IReadOnlyList<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)> processedInputs,
        IReadOnlySet<string> rolledBackFiles)
    {
        var preprocessor = new IteratorLoweringPreprocessor();
        var gotoEliminator = new GotoEliminator();
        foreach (var (relative, dest, _, _) in processedInputs)
        {
            if (rolledBackFiles.Contains(relative)) continue;

            var text = File.ReadAllText(dest);
            if (!text.Contains("yield", StringComparison.Ordinal)) continue;

            var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, dest, StringComparison.OrdinalIgnoreCase));
            if (tree == null) continue;

            var model = compilation.GetSemanticModel(tree);
            var result = preprocessor.Preprocess(text, model, new IteratorLoweringOptions());
            if (!result.Changed) continue;

            // The state machine emits goto/labels; the goto pass already ran
            // before linq-channel, so consume them here as it normally would
            // immediately after iterator-lowering.
            var output = result.OutputCode;
            var gotoResult = gotoEliminator.Eliminate(
                output, new GotoEliminatorOptions(Verbose: false, Strict: false));
            if (gotoResult.Changed) output = gotoResult.OutputCode;

            File.WriteAllText(dest, output, new UTF8Encoding(FileStartsWithBom(dest)));
            if (_verbose)
            {
                Console.WriteLine(
                    $"[linq-channel] iterator lowering: {relative} " +
                    $"({result.Statistics.IteratorMembersLowered} member(s))");
            }
        }
    }

    private static bool MayBeRewritten(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ForEachStatementSyntax:
                    return true;
                case InvocationExpressionSyntax invocation
                    when LinqRouteClassifier.HasLinqOperatorName(invocation):
                    return true;
            }
        }

        return false;
    }

    private CSharpCompilation BuildIntermediateCompilation(
        PassTreeContext tree,
        IReadOnlyList<(string Source, string Relative, string Dest)> pairs)
    {
        var trees = new List<SyntaxTree>();
        foreach (var (_, _, dest) in pairs)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                File.ReadAllText(dest),
                CreateParseOptions(tree),
                path: dest));
        }

        // Mirror SDK ImplicitUsings=enable (java-keywords precedent): one
        // synthesized tree per enabled project, appended compilation-only.
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot,
                Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length > 0)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                CreateParseOptions(tree),
                path: "__cs2j_global_usings.g.cs"));
        }

        return CSharpCompilation.Create(
            CompilationName,
            trees,
            GetMetadataReferences(),
            CreateCompilationOptions());
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    /// <summary>
    /// Refreshes the intermediate tree's NuGet assets and captures the per-project
    /// compile assemblies into <see cref="_enhancedReferences"/>. A restore failure
    /// is deliberately NOT reported here: the pass' own verification restore (which
    /// runs later and reports LC0050) stays the single owner of that diagnostic.
    /// No project graph (single-file / loose-directory layout) means nothing to
    /// restore; the conservative LC0010 path is unchanged there.
    /// </summary>
    private void EnsureEnhancedReferences(PassTreeContext tree)
    {
        if (tree.Layout.ProjectFiles.Count == 0)
        {
            return;
        }

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(
            tree.Layout, tree.IntermediateRoot);
        if (restoreTarget != null && ProjectPreprocessSource.RunDotNetRestore(
                restoreTarget, LogPrefix) != 0)
        {
            return;
        }

        _enhancedReferences = RestorePackageReferenceEnumerator.Enumerate(
            tree.IntermediateRoot, out var addedCount);
        if (_verbose && addedCount > 0)
        {
            Console.WriteLine($"[linq-channel] binding with {addedCount} restored package reference(s)");
        }
    }

    /// <summary>TPA basics plus the restored NuGet compile assemblies (M3 ruling 2).</summary>
    public override IReadOnlyCollection<MetadataReference> GetMetadataReferences()
    {
        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        if (_enhancedReferences.Count > 0)
        {
            references.AddRange(_enhancedReferences);
        }

        return references;
    }
}
