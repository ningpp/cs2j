using System.Text;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ThreadStaticRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the thread-static (C09) pass. Drives its own whole-tree
/// iteration like RecordReadOnlyPassAdapter: each round replays the originals,
/// runs the Core rollback loop (rewrite → recompile → attribute new errors →
/// exclude one plan or CS2J1200 whole-run rollback), then reconciles residual
/// [ThreadStatic] attributes against the final plan set (CS2J1220 gate).
/// Tree preparation and restore reuse the runner stage helpers.
/// </summary>
internal sealed class ThreadStaticPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-thread-static-src";

    private readonly ThreadStaticOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly List<ThreadStaticDiagnostic> _diagnostics = new();
    private ThreadStaticStatistics _statistics = new();
    private PassTreeContext? _currentTree;

    // One SyntaxTree per (path, content) shared by every compilation this pass
    // builds (round, baseline view, candidate view, residual view). Files that are
    // not rewritten keep their tree object, so they are parsed once instead of four
    // times and the unchanged trees are literally shared across the compilations.
    private readonly Dictionary<string, (string Content, SyntaxTree Tree)> _treeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private SyntaxTree? _implicitGlobalUsingsTree;

    // Lazily bound per-file baseline error keys for the candidate diff (see
    // <see cref="BaselineKeysFor"/>). Reset at the start of each diff.
    private CSharpCompilation? _baselineDiffCompilation;
    private Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>? _baselineDeclarationKeysByTree;
    private Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>? _baselineKeysByTree;

    public ThreadStaticPassAdapter(
        ThreadStaticOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ThreadStatic);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ThreadStaticStatistics Statistics => _statistics;
    public IReadOnlyList<ThreadStaticDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        _currentTree = tree;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;

        var sourceFiles = tree.CsFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        // Seed the intermediate tree with the pristine .cs sources...
        foreach (var pair in sourceFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Dest)!);
            tree.ResetScratchFile(pair.Source, pair.Dest);
        }

        // ...and carry the non-.cs assets (csproj, Directory.Build.*, ...) the
        // same way RecordReadOnlyPassAdapter does; .sln stays with preparation.
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

        var runnerInputs = sourceFiles
            .Select(p => new ThreadStaticSourceFile(p.Dest, File.ReadAllText(p.Source)))
            .ToList();

        var result = ThreadStaticRollbackRunner.Run(
            runnerInputs,
            _options,
            BuildCompilationFromTexts,
            DiffNewErrors);

        // Persist the final outputs; unchanged files are byte copies of the originals.
        foreach (var file in result.Files)
        {
            var pair = sourceFiles.First(p =>
                string.Equals(p.Dest, file.Path, StringComparison.OrdinalIgnoreCase));
            if (!file.Changed)
            {
                tree.ResetScratchFile(pair.Source, pair.Dest);
            }
            else
            {
                File.WriteAllText(pair.Dest, file.Output,
                    FileStartsWithBom(pair.Source) ? new UTF8Encoding(true) : new UTF8Encoding(false));
            }

            if (_verbose)
            {
                Console.WriteLine(
                    $"[thread-static] {(file.Changed ? "transformed" : "clean (copy)")}: "
                    + Path.GetRelativePath(sourceRoot, pair.Source));
            }
        }

        _statistics = result.Statistics;
        _diagnostics.AddRange(result.Diagnostics.Select(RelativizeDiagnostic));

        // Post-rewrite residual reconciliation only makes sense when the run was accepted:
        // after a whole-run rollback every field is back in its original form by design.
        // With no field plans at all there is nothing that could have been dropped, and
        // the residual report is necessarily empty, so the extra compilation is skipped.
        if (result.Success && result.FinalPlans.Count > 0)
        {
            RunResidualScan(result);
        }

        context.Success = result.Success;

        // Matches the standard spine: restore only exists to feed build/test
        // verification, so with --no-verify-build/--no-verify-tests the whole-tree
        // restore is pure overhead and is skipped.
        var restoreExitCode = (VerifyBuild || VerifyTests) ? context.Restore() : 0;
        if (restoreExitCode != 0)
        {
            context.Success = false;
        }

        var verified = await context.VerifyBuildAndTestsAsync();
        if (!verified)
        {
            context.Success = false;
        }
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("ThreadStaticPassAdapter drives the file loop itself.");

    // Standard .NET (net6+) ImplicitUsings set. The rollback loop builds its own
    // compilations from the parsed sources, so the SDK-generated
    // GlobalUsings.g.cs is absent; projects that rely on it (the common case)
    // would bind [ThreadStatic] to an error type and every field plan would be
    // silently missed. Mirrors ClosureLiftPassAdapter's synthesized tree.
    private const string ImplicitGlobalUsings =
        """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
        => new[] { CSharpSyntaxTree.ParseText(ImplicitGlobalUsings, CreateParseOptions(tree)) };

    /// <summary>
    /// Residual gate (design §4.2 step 5 / §7.4): residual attributes owned by an
    /// inactive plan (CS2J1201/1202/1203/1204/1205/1290) are expected and only
    /// surface as verbose info; unowned residual attributes mean the rewrite
    /// dropped a field and are reported CS2J1220 (Warning, Error under --strict).
    /// </summary>
    private void RunResidualScan(ThreadStaticRollbackResult result)
    {
        var compilation = BuildCompilationFromTexts(
            result.Files.Select(f => new ThreadStaticSourceFile(f.Path, f.Output)).ToList());
        var report = ThreadStaticResidualScanner.Scan(compilation, result.FinalPlans);

        _statistics.ResidualThreadStaticAttributes = report.Total;
        _statistics.OwnedResidualAttributes = report.Owned.Count;
        _statistics.UnownedResidualAttributes = report.Unowned.Count;

        if (_verbose && report.Owned.Count > 0)
        {
            foreach (var residual in report.Owned)
            {
                var lineSpan = residual.Location.GetLineSpan();
                _diagnostics.Add(new ThreadStaticDiagnostic(
                    ThreadStaticSeverity.Info,
                    ThreadStaticRule.Residual,
                    residual.OwnerCode ?? ThreadStaticCodes.ResidualFound,
                    $"[ThreadStatic] attribute retained for skipped plan {residual.OwnerCode} on "
                        + $"'{residual.FieldDisplayName}'.",
                    lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    residual.OwnerPlanId));
            }
        }

        if (report.Unowned.Count > 0)
        {
            var severity = _options.Strict
                ? ThreadStaticSeverity.Error
                : ThreadStaticSeverity.Warning;
            foreach (var residual in report.Unowned)
            {
                var lineSpan = residual.Location.GetLineSpan();
                _diagnostics.Add(new ThreadStaticDiagnostic(
                    severity,
                    ThreadStaticRule.Residual,
                    ThreadStaticCodes.ResidualFound,
                    $"Residual [ThreadStatic] attribute on '{residual.FieldDisplayName}' has no matching "
                        + "skipped field plan; the rewrite did not cover this field.",
                    lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    null));
            }
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Warning,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Error,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.RestoreOrBuildFailed,
            "dotnet build failed with exit code " + exitCode
                + " after thread-static preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Error,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after thread-static preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ThreadStaticSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ThreadStaticSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();

    // ---- in-memory compilation + error diffing (Core rollback loop callbacks) ----

    /// <summary>
    /// Parse (or reuse) the syntax tree for <paramref name="path"/> with the given
    /// content. A repeated call with byte-identical content returns the cached tree,
    /// which is what lets the baseline / candidate / residual compilations share the
    /// unmodified files' trees instead of reparsing the whole forest each time.
    /// </summary>
    private SyntaxTree TreeFor(string path, string content)
    {
        if (_treeCache.TryGetValue(path, out var entry) && entry.Content == content)
        {
            return entry.Tree;
        }

        var tree = CSharpSyntaxTree.ParseText(
            content, CreateParseOptions(_currentTree!), path: path);
        _treeCache[path] = (content, tree);
        return tree;
    }

    private CSharpCompilation BuildCompilationFromTexts(IReadOnlyList<ThreadStaticSourceFile> files)
    {
        var trees = new List<SyntaxTree>(
            files.Count + (_currentTree == null ? 0 : 1));
        foreach (var file in files)
        {
            trees.Add(TreeFor(file.Path, file.Content));
        }

        if (_currentTree != null)
        {
            trees.Add(_implicitGlobalUsingsTree ??=
                CSharpSyntaxTree.ParseText(ImplicitGlobalUsings, CreateParseOptions(_currentTree)));
        }

        return CSharpCompilation.Create(
            CompilationName,
            trees,
            GetMetadataReferences(),
            CreateCompilationOptions());
    }

    /// <summary>
    /// Candidate errors the ORIGINAL sources did not already report. Only the trees
    /// this round actually rewrote are examined: every other file is byte-identical
    /// to its baseline source (and shares the same tree object), so its diagnostics
    /// are unchanged and cannot contribute a new error. The thread-static rewrite
    /// edits the field declaration and its resolved reference sites, all of which
    /// live in the rewritten (touched) files, so a new error can only be located in
    /// one of them. Mirrors the repo's established replacement for a whole-compilation
    /// GetDiagnostics (partial-merge <c>5acf4a51</c>, extension-methods <c>d3e50ced</c>,
    /// struct-value-semantics <c>aaebe9b5</c>).
    /// </summary>
    private IReadOnlyList<ThreadStaticCompileError> DiffNewErrors(
        IReadOnlyList<ThreadStaticRewrittenFile> baseline,
        IReadOnlyList<ThreadStaticRewrittenFile> candidate)
    {
        var rewrittenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidate)
        {
            if (file.Changed)
            {
                rewrittenKeys.Add(Normalize(file.Path));
            }
        }

        if (rewrittenKeys.Count == 0)
        {
            return Array.Empty<ThreadStaticCompileError>();
        }

        var candidateCompilation = BuildCompilationFromTexts(
            candidate.Select(f => new ThreadStaticSourceFile(f.Path, f.Output)).ToList());
        var baselineCompilation = BuildCompilationFromTexts(
            baseline.Select(f => new ThreadStaticSourceFile(f.Path, f.Source)).ToList());

        _baselineDiffCompilation = baselineCompilation;
        _baselineDeclarationKeysByTree = null;
        _baselineKeysByTree = new Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>();

        var baselineTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in baselineCompilation.SyntaxTrees)
        {
            baselineTrees.TryAdd(Normalize(tree.FilePath), tree);
        }

        var result = new List<ThreadStaticCompileError>();

        // Only the rewritten trees' semantic models are bound. A whole-compilation
        // GetDiagnostics (or even just the compilation-wide declaration stage) binds
        // every tree in the forest; the per-tree view is the repo's established
        // substitute (extension-methods d3e50ced, struct-value-semantics aaebe9b5).
        // The thread-static rewrite only ever edits a field declaration and its
        // resolved reference sites inside the rewritten files (the synthesized cctor
        // is inserted into the declaring type's own tree, and is suppressed whenever
        // any part of the type already declares a cctor), so it cannot introduce a
        // cross-tree declaration conflict that a per-tree model would miss.
        foreach (var tree in candidateCompilation.SyntaxTrees)
        {
            if (!rewrittenKeys.Contains(Normalize(tree.FilePath)))
            {
                continue;
            }

            foreach (var diagnostic in candidateCompilation.GetSemanticModel(tree).GetDiagnostics())
            {
                Consider(diagnostic);
            }
        }

        return result
            .OrderBy(e => e.FilePath, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)
            .ToList();

        void Consider(Diagnostic diagnostic)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error
                || diagnostic.Location.SourceTree is not { } tree)
            {
                return;
            }

            var key = Normalize(tree.FilePath);
            if (!rewrittenKeys.Contains(key))
            {
                return;
            }

            if (baselineTrees.TryGetValue(key, out var baselineTree)
                && BaselineKeysFor(baselineTree).Contains((diagnostic.Descriptor.Id, diagnostic.GetMessage())))
            {
                return;
            }

            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            result.Add(new ThreadStaticCompileError(
                tree.FilePath,
                diagnostic.Location.SourceSpan,
                diagnostic.Descriptor.Id,
                diagnostic.GetMessage(),
                position.Line + 1,
                position.Character + 1));
        }
    }

    /// <summary>
    /// Per-file baseline (id, message) keys over the ORIGINAL sources: the
    /// compilation-wide declaration diagnostics grouped by tree (bound once) plus
    /// each file's own SemanticModel diagnostics (bound on first use). Only the
    /// rewritten files are ever queried, so this stays a few-file cost.
    /// </summary>
    private HashSet<(string Id, string Message)> BaselineKeysFor(SyntaxTree baselineTree)
    {
        var keysByTree = _baselineKeysByTree!;
        if (keysByTree.TryGetValue(baselineTree, out var cached))
        {
            return cached;
        }

        if (_baselineDeclarationKeysByTree is null)
        {
            var byTree = new Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>();
            foreach (var diagnostic in _baselineDiffCompilation!.GetDeclarationDiagnostics())
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error
                    || diagnostic.Location.SourceTree is not { } declTree)
                {
                    continue;
                }

                if (!byTree.TryGetValue(declTree, out var set))
                {
                    set = new HashSet<(string, string)>();
                    byTree[declTree] = set;
                }

                set.Add((diagnostic.Descriptor.Id, diagnostic.GetMessage()));
            }

            _baselineDeclarationKeysByTree = byTree;
        }

        var keys = new HashSet<(string Id, string Message)>();
        if (_baselineDeclarationKeysByTree.TryGetValue(baselineTree, out var declarationKeys))
        {
            keys.UnionWith(declarationKeys);
        }

        foreach (var diagnostic in _baselineDiffCompilation!.GetSemanticModel(baselineTree).GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree != null)
            {
                keys.Add((diagnostic.Descriptor.Id, diagnostic.GetMessage()));
            }
        }

        keysByTree[baselineTree] = keys;
        return keys;
    }

    private string Normalize(string path) => path.Replace('\\', '/');

    private ThreadStaticDiagnostic RelativizeDiagnostic(ThreadStaticDiagnostic diagnostic)
    {
        if (string.IsNullOrEmpty(diagnostic.FilePath) || _currentTree == null) return diagnostic;
        var relative = Path.GetRelativePath(_currentTree.IntermediateRoot, diagnostic.FilePath);
        return diagnostic with { FilePath = relative.Replace('\\', '/') };
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}
