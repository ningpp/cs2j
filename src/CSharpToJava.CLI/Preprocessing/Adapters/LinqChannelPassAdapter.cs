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

        var desugarSw = System.Diagnostics.Stopwatch.StartNew();
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
        // Stage-1 trees are captured in memory so the stage-2 compilation can
        // be assembled without re-reading/re-parsing every file: unchanged
        // files reuse the tree parsed over the pristine bytes, changed files
        // wrap the desugared root (CSharpSyntaxTree.Create does no parsing).
        var stage1Trees = new SyntaxTree[pairs.Count];

        // ---- stage 1: whole-tree query desugaring (pure syntax) ----
        // The prepare step's strict syntax gate already parsed every .cs file
        // under the SAME parse options and cached the trees (source-path
        // keyed); reuse them instead of re-parsing the whole tree — an entry
        // is only reused for byte-identical content, and the destination path
        // is stamped on via WithFilePath (a cheap path-only clone).
        var parseSignature = ParsedTreeCache.Signature(parseOptions);
        Parallel.For(0, pairs.Count, i =>
        {
            var (source, _, dest) = pairs[i];
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var originalText = File.ReadAllText(source);
            var originalTree = ParsedTreeCache.TryGet(parseSignature, source, originalText, out var cached)
                ? cached.WithFilePath(dest)
                : CSharpSyntaxTree.ParseText(originalText, parseOptions, path: dest);
            var originalRoot = originalTree.GetRoot();
            var (desugaredRoot, desugaredCount) = LinqChannelRewriter.DesugarQueries(originalRoot);
            stage1Desugared[i] = desugaredCount;
            stage1Changed[i] = !ReferenceEquals(desugaredRoot, originalRoot)
                && !desugaredRoot.IsEquivalentTo(originalRoot);

            if (stage1Changed[i])
            {
                File.WriteAllText(
                    dest,
                    desugaredRoot.ToFullString(),
                    new UTF8Encoding(FileStartsWithBom(source)));
                stage1Trees[i] = CSharpSyntaxTree.Create(
                    (CompilationUnitSyntax)desugaredRoot, parseOptions, path: dest);
            }
            else
            {
                tree.ResetScratchFile(source, dest);
                stage1Trees[i] = originalTree;
            }
        });

        _desugaredQueryCount = stage1Desugared.Sum();
        desugarSw.Stop();
        context.Timings["desugar"] = desugarSw.Elapsed;

        // ---- stage 2: recompile the desugared tree, route + rewrite per file ----
        var compileSw = System.Diagnostics.Stopwatch.StartNew();
        var compilation = CreateCompilation(stage1Trees, BuildGlobalUsingsTree(tree));
        var channel = new LinqChannelRewriter(_options);
        var success = true;

        // Stage-2 inputs retained for the §7.10 AfterFiles residual scan:
        // (relative path, stage-2 semantic model, post-stage-1 syntax root).
        var processedInputs = new List<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)>();
        var rolledBackFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Performance: the per-file channel work is independent (each file gets
        // its own semantic model and its own rewriter instance, and nothing in
        // the pipeline mutates shared state), so it runs in parallel. Every
        // observable side effect — statistics merge, diagnostic order, the
        // processed-input list and the on-disk writes — is applied afterwards
        // in file order, which keeps the result byte-identical to the
        // sequential loop this replaces.
        var treeByPath = IndexSyntaxTreesByPath(compilation);
        compileSw.Stop();
        context.Timings["compile"] = compileSw.Elapsed;
        var stage2Sw = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new Stage2Outcome?[pairs.Count];
        var stage2Failures = new Exception?[pairs.Count];
        Parallel.For(0, pairs.Count, i =>
        {
            var (_, relative, dest) = pairs[i];
            if (!treeByPath.TryGetValue(dest, out var syntaxTree))
            {
                return;
            }

            var desugaredRoot = syntaxTree.GetRoot();

            // R9 fast-skip: files with neither a LINQ-operator invocation nor a
            // foreach cannot be touched by either LinqRewriter entry point, so
            // skip semantic planning/rewrite for them. (plan.HasLinqChains is
            // the semantic signal used inside ProcessFile; foreach stays in the
            // gate because the foreach-rewrap path can rewrite plain
            // List/array collections.)
            if (!MayBeRewritten(desugaredRoot))
            {
                return;
            }

            var reportPath = relative.Replace('\\', '/');
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            try
            {
                var injected = Lc0011FaultInjector?.Invoke(reportPath);
                if (injected is not null)
                {
                    throw injected;
                }

                var result = channel.ProcessFile(
                    semanticModel,
                    desugaredRoot,
                    reportPath);
                outcomes[i] = new Stage2Outcome(
                    semanticModel, desugaredRoot, result, null);
            }
            catch (LinqRouteInvariantException ex)
            {
                outcomes[i] = new Stage2Outcome(
                    semanticModel, desugaredRoot, null, ex);
            }
            catch (Exception ex)
            {
                // Deferred so the pass still fails with the ORIGINAL exception
                // type/message instead of Parallel.For's AggregateException.
                stage2Failures[i] = ex;
            }
        });

        RethrowFirstFailure(stage2Failures);

        for (var i = 0; i < pairs.Count; i++)
        {
            if (outcomes[i] is not { } outcome)
            {
                continue;
            }

            var (source, relative, dest) = pairs[i];
            var reportPath = relative.Replace('\\', '/');

            if (outcome.Invariant is not null)
            {
                // LC0011 (design §4.3/§6): the three-way gate lost a node and
                // a preserved route reached the expansion branch / fabricated
                // syntax-name fallback. Roll the file back to its ORIGINAL
                // source bytes (discarding even stage-1 desugaring for it) so
                // the intermediate tree stays compilable, record the Error and
                // fail the pass; other files are still processed in this run.
                tree.ResetScratchFile(source, dest);
                _diagnostics.Add(outcome.Invariant.ToDiagnostic());
                rolledBackFiles.Add(relative);
                success = false;
                if (_verbose)
                {
                    Console.WriteLine($"[linq-channel] LC0011 rollback: {relative}");
                }

                continue;
            }

            var result = outcome.Result!;
            processedInputs.Add((relative, dest, outcome.SemanticModel, outcome.InputRoot));

            _statistics.MergeFrom(result.Statistics);
            _diagnostics.AddRange(result.Diagnostics);

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

        stage2Sw.Stop();
        context.Timings["stage2"] = stage2Sw.Elapsed;
        var residualSw = System.Diagnostics.Stopwatch.StartNew();

        // ---- §7.10 AfterFiles residual gates (whole-tree recompile) ----
        // Files rolled back to their ORIGINAL source by LC0011 are excluded
        // (they intentionally never went through stage 1/stage 2).
        //
        // Incremental rebuild: only files the stage-2 rewrite changed (plus
        // LC0011 rollbacks restored to their original bytes) differ from the
        // stage-2 compilation, so replace just those trees instead of
        // re-reading and re-parsing the whole tree. Replaced trees are built
        // from the in-memory rewritten roots (CSharpSyntaxTree.Create performs
        // no parsing), and their text is exactly the bytes written to disk.
        var postChannelCompilation = compilation;
        for (var i = 0; i < pairs.Count; i++)
        {
            if (!treeByPath.TryGetValue(pairs[i].Dest, out var oldTree))
            {
                continue;
            }

            SyntaxTree? newTree = null;
            if (rolledBackFiles.Contains(pairs[i].Relative))
            {
                newTree = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(pairs[i].Dest), parseOptions, path: pairs[i].Dest);
            }
            else if (outcomes[i]?.Result is { RewriteChanged: true } rewritten)
            {
                newTree = CSharpSyntaxTree.Create(
                    (CompilationUnitSyntax)rewritten.RewrittenRoot, parseOptions, path: pairs[i].Dest);
            }

            if (newTree is not null)
            {
                postChannelCompilation = postChannelCompilation.ReplaceSyntaxTree(oldTree, newTree);
            }
        }

        var postChannelTreeByPath = IndexSyntaxTreesByPath(postChannelCompilation);

        if (processedInputs.Count > 0 && rolledBackFiles.Count < processedInputs.Count)
        {
            var outputTreeByPath = postChannelTreeByPath;
            var residualOutcomes = new ResidualOutcome?[processedInputs.Count];
            var residualFailures = new Exception?[processedInputs.Count];
            Parallel.For(0, processedInputs.Count, index =>
            {
                var (relative, dest, inputModel, inputRoot) = processedInputs[index];
                if (rolledBackFiles.Contains(relative))
                {
                    return;
                }

                if (!outputTreeByPath.TryGetValue(dest, out var outputTree))
                {
                    return;
                }

                try
                {
                    var outputRoot = outputTree.GetRoot();
                    var outputModel = postChannelCompilation.GetSemanticModel(outputTree);
                    var reportPath = relative.Replace('\\', '/');

                    // Gates ① + ⑤: rerunning stage 2 over the materialized
                    // output must move nothing — any in-support-matrix
                    // Enumerable chain left behind WOULD move, so a zero-diff
                    // second pass is the residual=0 and idempotency proof.
                    // Statistics are collected:false — this pass's counters
                    // are discarded by the caller; only the rewrite outcome
                    // (RewriteChanged / SecondPassDesugaredCount) is consumed.
                    var secondPass = channel.ProcessFile(
                        outputModel, outputRoot, reportPath, collectStatistics: false);

                    // Gates ②③④: preserved-call stream identity + helper placement.
                    var findings = LinqChannelResidualScanner.ScanFile(
                        inputModel,
                        inputRoot,
                        outputModel,
                        outputRoot,
                        reportPath,
                        _options.Strict);

                    residualOutcomes[index] = new ResidualOutcome(
                        secondPass,
                        findings,
                        reportPath,
                        secondPass.RewriteChanged
                            || secondPass.SecondPassDesugaredCount != 0
                            // A structurally unchanged rewrite hands back the very
                            // node it was given; only then is the (strict, trivia-
                            // sensitive) text comparison meaningful work.
                            || (!ReferenceEquals(outputRoot, secondPass.RewrittenRoot)
                                && !string.Equals(
                                    outputRoot.ToFullString(),
                                    secondPass.RewrittenRoot.ToFullString(),
                                    StringComparison.Ordinal)));
                }
                catch (Exception ex)
                {
                    residualFailures[index] = ex;
                }
            });

            RethrowFirstFailure(residualFailures);

            for (var index = 0; index < residualOutcomes.Length; index++)
            {
                if (residualOutcomes[index] is not { } residual)
                {
                    continue;
                }

                var secondPass = residual.SecondPass;
                if (residual.IdempotencyMismatch)
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
                        residual.ReportPath,
                        null));
                }

                // Gates ②③④: preserved-call stream identity + helper placement.
                if (residual.Findings.Count > 0)
                {
                    _statistics.ResidualFindings += residual.Findings.Count;
                    _diagnostics.AddRange(residual.Findings);
                }
            }
        }

        residualSw.Stop();
        context.Timings["residual"] = residualSw.Elapsed;
        var lowerSw = System.Diagnostics.Stopwatch.StartNew();
        var closureLiftSw = System.Diagnostics.Stopwatch.StartNew();

        // The stock procedural LINQ rewriter emits iterator methods
        // (`..._ProceduralLinqN`) with `yield return` statements. The
        // iterator-lowering pass has already run earlier in the chain (it must
        // precede eliminate-goto), so those freshly generated iterators would
        // otherwise reach the Java converter as residual yield. Lower them on
        // the materialized stage-2 output before the verification step.
        var loweringCompilation = postChannelCompilation;
        var loweringTreeByPath = postChannelTreeByPath;

        // The verification restore only touches obj/ assets and never the .cs
        // tree, so it overlaps the tail rewriting phases below instead of
        // serializing after them; the exit code is consumed exactly as before
        // (the report is already on disk, and diagnostics / verify / cleanup
        // all happen after the await).
        var restoreTask = System.Threading.Tasks.Task.Run(() => context.Restore());

        if (processedInputs.Count > 0)
        {
            // The procedural rewriter also emits lambdas (e.g. a projection
            // nested inside a yielding foreach). Lift those closure shapes the
            // same way the earlier closure-lift pass would have, then refresh
            // the semantic picture for the state-machine analysis. The lift
            // runs over the SAME compilation as the residual gates (nothing
            // was written in between), and only its changed trees are swapped
            // into the lowering compilation — no full-tree re-parse.
            var lifted = LiftGeneratedClosures(
                tree,
                processedInputs,
                rolledBackFiles,
                postChannelCompilation,
                postChannelTreeByPath);

            foreach (var (liftedDest, liftedCode) in lifted)
            {
                loweringCompilation = loweringCompilation.ReplaceSyntaxTree(
                    loweringTreeByPath[liftedDest],
                    CSharpSyntaxTree.ParseText(liftedCode, parseOptions, path: liftedDest));
            }

            if (lifted.Count > 0)
            {
                loweringTreeByPath = IndexSyntaxTreesByPath(loweringCompilation);
            }
        }

        closureLiftSw.Stop();
        context.Timings["closure-lift"] = closureLiftSw.Elapsed;
        var iteratorLowerSw = System.Diagnostics.Stopwatch.StartNew();
        if (processedInputs.Count > 0)
        {
            LowerGeneratedIterators(
                loweringCompilation, loweringTreeByPath, processedInputs, rolledBackFiles);
        }

        iteratorLowerSw.Stop();
        context.Timings["iterator-lower"] = iteratorLowerSw.Elapsed;
        lowerSw.Stop();
        context.Timings["lower"] = lowerSw.Elapsed;

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
        var restoreVerifySw = System.Diagnostics.Stopwatch.StartNew();
        var restoreExitCode = await restoreTask;
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
        restoreVerifySw.Stop();
        context.Timings["restore-verify"] = restoreVerifySw.Elapsed;
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

    private static CompilationUnitSyntax ParseCompilationUnit(string code)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();

    /// <summary>
    /// Conservative host-identity key for the closure-lift parallelism
    /// partition: the namespace chain plus the nested type-name chain of the
    /// declaration. Two files whose semantic host keys (fully qualified type
    /// displays) are equal necessarily produce equal chain keys, so grouping
    /// by chain key never splits files that must share one naming table.
    /// </summary>
    private static string TypeChainKey(BaseTypeDeclarationSyntax type)
    {
        var typeChain = new Stack<string>();
        for (var node = (SyntaxNode?)type; node is BaseTypeDeclarationSyntax decl; node = decl.Parent)
        {
            typeChain.Push(decl.Identifier.Text);
        }

        var namespaces = new List<string>();
        for (var node = type.Parent; node is not null; node = node.Parent)
        {
            if (node is BaseNamespaceDeclarationSyntax ns)
            {
                namespaces.Add(ns.Name.ToString());
            }
        }

        namespaces.Reverse();
        return string.Join(".", namespaces) + ":" + string.Join("+", typeChain);
    }

    /// <summary>
    /// Lifts lambdas emitted by the procedural LINQ rewriter in files that
    /// still contain `yield`. Those files were closure-lifted before this pass
    /// ran, so the generated shapes have not been through that stage.
    /// Deterministic naming requires files that may share a host TYPE (partial
    /// parts) to be lifted in file order through one naming table — so files
    /// are grouped by their declared type-name sets (a conservative superset
    /// of semantic host keys) and the groups run in parallel, each with its
    /// own table and original file order inside. Groups never share a host,
    /// so the merged result equals the single-table sequential run.
    /// Returns the (destination, output) pairs that changed so the caller can
    /// swap just those trees into the next compilation.
    /// </summary>
    private List<(string Dest, string OutputCode)> LiftGeneratedClosures(
        PassTreeContext tree,
        IReadOnlyList<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)> processedInputs,
        IReadOnlySet<string> rolledBackFiles,
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treeByPath)
    {
        // Candidates: still-yield files with a tree in the compilation.
        var candidates = new List<(string Relative, string Dest, string Text, SyntaxTree Tree)>();
        for (var i = 0; i < processedInputs.Count; i++)
        {
            var (relative, dest, _, _) = processedInputs[i];
            if (rolledBackFiles.Contains(relative)) continue;

            var text = File.ReadAllText(dest);
            if (!text.Contains("yield", StringComparison.Ordinal)) continue;

            if (!treeByPath.TryGetValue(dest, out var treeNode)) continue;

            candidates.Add((relative, dest, text, treeNode));
        }

        if (candidates.Count == 0)
        {
            return new List<(string Dest, string OutputCode)>();
        }

        // Union-find over full type-chain keys (namespace chain + nested type
        // chain): two files can share a semantic host type only when both
        // chains match, so chain keys are a safe — and much finer — partition
        // than simple names (the same type name in different namespaces no
        // longer serializes unrelated files).
        var parent = new int[candidates.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

        var namesByCandidate = new List<HashSet<string>>(candidates.Count);
        foreach (var (_, _, _, syntaxTree) in candidates)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in syntaxTree.GetRoot().DescendantNodes()
                         .OfType<BaseTypeDeclarationSyntax>())
            {
                names.Add(TypeChainKey(type));
            }

            namesByCandidate.Add(names);
        }

        var firstByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var name in namesByCandidate[i])
            {
                if (firstByName.TryGetValue(name, out var other))
                {
                    Union(i, other);
                }
                else
                {
                    firstByName[name] = i;
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
            list.Add(i); // ascending order == original file order within the group
        }

        var lifted = new (string Dest, string OutputCode)?[candidates.Count];
        Parallel.ForEach(groups.Values, groupIndices =>
        {
            // One preprocessor (naming table) per group; groups are disjoint
            // by host-name, and file order inside the group is preserved.
            var preprocessor = new ClosureLiftPreprocessor();
            foreach (var index in groupIndices)
            {
                var candidate = candidates[index];
                var result = preprocessor.Preprocess(
                    candidate.Text,
                    compilation.GetSemanticModel(candidate.Tree),
                    new ClosureLiftOptions(Strict: false));
                if (result.Changed)
                {
                    lifted[index] = (candidate.Dest, result.OutputCode);
                }
            }
        });

        var changed = new List<(string Dest, string OutputCode)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (lifted[i] is { } pair)
            {
                File.WriteAllText(pair.Dest, pair.OutputCode, new UTF8Encoding(FileStartsWithBom(pair.Dest)));
                changed.Add(pair);
            }
        }

        return changed;
    }

    /// <summary>
    /// Runs the iterator-lowering preprocessor over stage-2 files that still
    /// contain `yield` (the procedural LINQ rewriter's generated iterators).
    /// Original yields were handled by the earlier iterator-lowering pass;
    /// only output written by this pass can reach here. The per-file work is
    /// independent (stateless preprocessors, one semantic model per file, one
    /// distinct destination file each), so it fans out; observable effects
    /// (writes, verbose lines) are applied in file order afterwards.
    /// </summary>
    private void LowerGeneratedIterators(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treeByPath,
        IReadOnlyList<(string Relative, string Dest, SemanticModel Model, SyntaxNode InputRoot)> processedInputs,
        IReadOnlySet<string> rolledBackFiles)
    {
        var loweringOutputs = new (string Relative, string Output, int MembersLowered)?[processedInputs.Count];
        var failures = new Exception?[processedInputs.Count];
        // Stateless per call; one shared instance avoids per-file allocation.
        var gotoEliminator = new GotoEliminator();
        Parallel.For(0, processedInputs.Count, index =>
        {
            var (relative, dest, _, _) = processedInputs[index];
            if (rolledBackFiles.Contains(relative)) return;

            var text = File.ReadAllText(dest);
            if (!text.Contains("yield", StringComparison.Ordinal)) return;

            if (!treeByPath.TryGetValue(dest, out var treeNode)) return;

            try
            {
                var model = compilation.GetSemanticModel(treeNode);
                var preprocessor = new IteratorLoweringPreprocessor();
                var result = preprocessor.Preprocess(text, model, new IteratorLoweringOptions());
                if (!result.Changed) return;

                // The state machine emits goto/labels; the goto pass already ran
                // before linq-channel, so consume them here as it normally would
                // immediately after iterator-lowering.
                // Node-level chaining: the lowering output tree is handed over
                // directly (a re-parse of OutputCode is an equivalent tree), so
                // the goto eliminator skips re-parsing these large files.
                var output = result.OutputCode;
                var gotoResult = gotoEliminator.Eliminate(
                    result.FormattedRoot ?? ParseCompilationUnit(output),
                    new GotoEliminatorOptions(Verbose: false, Strict: false));
                if (gotoResult.Changed)
                {
                    output = gotoResult.OutputCode;
                }

                loweringOutputs[index] = (relative, output, result.Statistics.IteratorMembersLowered);
            }
            catch (Exception ex)
            {
                failures[index] = ex;
            }
        });

        RethrowFirstFailure(failures);

        for (var index = 0; index < loweringOutputs.Length; index++)
        {
            if (loweringOutputs[index] is not { } lowered)
            {
                continue;
            }

            var (_, dest, _, _) = processedInputs[index];
            File.WriteAllText(dest, lowered.Output, new UTF8Encoding(FileStartsWithBom(dest)));
            if (_verbose)
            {
                Console.WriteLine(
                    $"[linq-channel] iterator lowering: {lowered.Relative} " +
                    $"({lowered.MembersLowered} member(s))");
            }
        }
    }

    /// <summary>
    /// Path-keyed view of a compilation's syntax trees. The per-file loops look
    /// a file up by its destination path; a linear scan per file made that
    /// O(files²) on whole-SDK trees, so the lookup is indexed once per
    /// compilation instead.
    /// </summary>
    private static Dictionary<string, SyntaxTree> IndexSyntaxTreesByPath(CSharpCompilation compilation)
    {
        var byPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            byPath[syntaxTree.FilePath] = syntaxTree;
        }

        return byPath;
    }

    /// <summary>
    /// Re-throws the first deferred per-file failure with its original stack
    /// trace, so a parallelised loop fails exactly like the sequential one it
    /// replaced (no <see cref="AggregateException"/> wrapper, deterministic
    /// first-failure selection by file order).
    /// </summary>
    private static void RethrowFirstFailure(Exception?[] failures)
    {
        foreach (var failure in failures)
        {
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
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

    /// <summary>
    /// Mirror SDK ImplicitUsings=enable (java-keywords precedent): one
    /// synthesized tree per enabled project, appended compilation-only.
    /// Built once per pass and shared by every compilation derived from it
    /// (the tree is immutable, and reuse keeps incremental
    /// ReplaceSyntaxTree chains referencing identical tree objects).
    /// </summary>
    private SyntaxTree? BuildGlobalUsingsTree(PassTreeContext tree)
    {
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

        if (globalUsings.Length == 0)
        {
            return null;
        }

        return CSharpSyntaxTree.ParseText(
            globalUsings.ToString(),
            CreateParseOptions(tree),
            path: "__cs2j_global_usings.g.cs");
    }

    private CSharpCompilation CreateCompilation(
        IReadOnlyList<SyntaxTree> trees,
        SyntaxTree? globalUsingsTree)
    {
        var allTrees = new List<SyntaxTree>(trees.Count + 1);
        allTrees.AddRange(trees);
        if (globalUsingsTree is not null)
        {
            allTrees.Add(globalUsingsTree);
        }

        return CSharpCompilation.Create(
            CompilationName,
            allTrees,
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

    /// <summary>Per-file result of the parallel stage-2 rewrite pass.</summary>
    private sealed record Stage2Outcome(
        SemanticModel SemanticModel,
        SyntaxNode InputRoot,
        LinqChannelRewriteResult? Result,
        LinqRouteInvariantException? Invariant);

    /// <summary>Per-file result of the parallel §7.10 residual gate pass.</summary>
    private sealed record ResidualOutcome(
        LinqChannelRewriteResult SecondPass,
        List<LinqChannelDiagnostic> Findings,
        string ReportPath,
        bool IdempotencyMismatch);
}
