using System.Text;
using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for partial-merge (C20). Drives its own whole-tree execution like
/// JavaKeywordsPassAdapter (design §5): prepare/copy → whole-tree semantic
/// analysis → per-file deterministic rewrites → restore → build/test verify.
/// Replays from originals each round: baseline snapshot, recompile-diff,
/// per-group/whole-tree rollback, residual gate.
/// </summary>
internal sealed class PartialMergePassAdapter : ProjectPassAdapter
{
    private readonly PartialMergeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly List<PartialMergeDiagnostic> _diagnostics = new();

    private PartialMergePlan? _acceptedPlan;
    private PartialMergePlan? _lastPlan;

    public PartialMergePassAdapter(
        PartialMergeOptions options,
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

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.PartialMerge);

    public override bool Verbose => _verbose;
    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == PartialMergeSeverity.Error);
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated global usings;
    /// synthesize them so semantic analysis and the candidate recompile resolve
    /// names identically to the real build (mirrors TuplePassAdapter).
    /// </summary>
    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        const string syntheticPath = "__cs2j_global_usings.g.cs";
        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath),
        };
    }

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;

        // 1. Explicit 1:1 copy; the shared analysis compilation is parsed once from
        // the untouched source root and reused by every round, so the whole tree is
        // parsed (and re-parsed) exactly once instead of per round / per
        // compilation.
        CopyInputs(tree);
        var analysisCompilation = context.BuildStandardCompilation();

        // The whole-tree baseline key set is bound at most once, lazily, and only
        // to confirm candidate errors the per-file baseline could not classify
        // (see DiffErrors). A round that rewrites nothing never asks for it, so the
        // pass stays off the whole-tree GetDiagnostics path entirely. The per-file
        // index shares the compilation-wide declaration-diagnostics bind with the
        // analyzer's baseline-skip gate.
        HashSet<(string File, string Id, string Message)>? fullBaselineKeys = null;
        HashSet<(string File, string Id, string Message)> FullBaselineKeys()
            => fullBaselineKeys ??= new HashSet<(string File, string Id, string Message)>(
                analysisCompilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree != null)
                    .Select(d => (
                        NormalizePath(d.Location.SourceTree!.FilePath, tree.SourceRoot),
                        d.Descriptor.Id,
                        d.GetMessage())));

        var baselineKeys = new BaselineKeyIndex(analysisCompilation);

        var preprocessor = new PartialMergePreprocessor(_options);
        var excludedGroups = new HashSet<string>(StringComparer.Ordinal);
        var success = true;
        CSharpCompilation? candidate = null;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 2. Every round starts from originals (replay, like JavaKeywords L101-103).
            // Round 0 does not need the reset: the tree was copied to the
            // intermediate root before the analysis compilation was built and
            // nothing wrote to it since. Later rounds must re-seed after a
            // rollback restored a subset of the files.
            if (iteration > 0)
            {
                CopyInputs(tree);
            }

            // Reuse the baseline build: the source root never mutates during the
            // pass, so its diagnostics are the same every round. The analyzer binds
            // the errors of the candidate-part files itself, lazily.
            var plan = preprocessor.Analyze(
                analysisCompilation, tree.SourceRoot);
            _lastPlan = plan;
            foreach (var group in plan.Groups
                         .Where(g => excludedGroups.Contains(g.TypeFullName)))
            {
                group.RolledBack = true;
            }

            // Rollback propagates down the containing chain: a nested group emits
            // no file edits and its merged node only rides a merged container, so
            // it is rolled back by its container's restore and must not stay
            // active (otherwise it breaks error attribution and the residual
            // gate). Exclude it for future replay rounds as well.
            foreach (var blocked in PartialMergePreprocessor.EnclosedByInactiveGroup(plan.Groups))
            {
                blocked.RolledBack = true;
                excludedGroups.Add(blocked.TypeFullName);
            }

            // 3. Per-file rewrites; filter at edit granularity (part spans), so a
            // file shared by an active and a rolled-back group only replays the
            // active edits. Files with no active edit are already copied. The
            // active part spans are indexed once by file instead of being re-scanned
            // for every file of the tree (O(files x groups x parts)).
            var activePartSpansByFile = plan.ActiveGroups
                .SelectMany(group => group.Parts)
                .GroupBy(part => part.SourceFilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    byFile => byFile.Key,
                    byFile => byFile.Select(part => part.Declaration.FullSpan).ToHashSet(),
                    StringComparer.OrdinalIgnoreCase);
            var rewriter = new PartialMergeRewriter();
            var rewrittenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var csFile in tree.CsFiles)
            {
                var edits = plan.EditsByFile[csFile];
                if (!activePartSpansByFile.TryGetValue(csFile, out var activePartSpans))
                {
                    continue;
                }

                var activeEdits = edits
                    .Where(e => activePartSpans.Contains(e.Span))
                    .ToList();
                if (activeEdits.Count == 0)
                {
                    continue;
                }

                var source = await File.ReadAllTextAsync(csFile);
                var output = rewriter.Apply(source, activeEdits);
                var destination = Path.Combine(
                    tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, csFile));
                File.WriteAllText(destination, output,
                    FileStartsWithBom(csFile) ? new UTF8Encoding(true) : new UTF8Encoding(false));
                rewrittenFiles.Add(csFile);
            }

            if (rewrittenFiles.Count == 0)
            {
                // Nothing was rewritten: the intermediate tree is byte-identical
                // to the analysis compilation's content, so the candidate
                // recompile would yield zero new errors against the baseline and
                // the residual gate would read exactly the same source. Accept
                // and reuse the baseline compilation in place of the candidate
                // instead of recompiling and re-binding the whole tree.
                _acceptedPlan = plan;
                candidate = analysisCompilation;
                break;
            }

            // 4. Candidate recompile + baseline diff.
            candidate = BuildIntermediateCompilation(tree, rewrittenFiles);
            var rewrittenKeys = new HashSet<string>(
                rewrittenFiles.Select(file => NormalizePath(file, tree.SourceRoot)),
                StringComparer.OrdinalIgnoreCase);
            var newErrors = DiffErrors(tree, candidate, rewrittenKeys, baselineKeys, FullBaselineKeys);

            if (newErrors.Count == 0)
            {
                _acceptedPlan = plan;
                break;
            }

            // 5. Attribute every new error to exactly one active group (design D9).
            var groupsToRollback = new HashSet<TypeGroupPlan>();
            var attributable = true;
            foreach (var error in newErrors)
            {
                var hits = AttributeToGroups(error, plan);
                if (hits.Count != 1)
                {
                    attributable = false;
                    break;
                }

                groupsToRollback.Add(hits[0]);
            }

            if (!attributable || groupsToRollback.Count == 0)
            {
                // Zero/multi attribution → whole-tree rollback (CS2J2002).
                CopyInputs(tree);
                var first = newErrors[0];
                _diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Error,
                    PartialMergeCodes.WholeTreeRolledBack,
                    "whole-tree validation failed and the new error (" +
                    first.Descriptor.Id + ": " + first.GetMessage() +
                    ") could not be attributed to a single group; all changes were rolled back."));
                success = false;
                break;
            }

            // 6. Group rollback (CS2J2001): restore every touched file to original bytes.
            foreach (var group in groupsToRollback)
            {
                group.RolledBack = true;
                excludedGroups.Add(group.TypeFullName);
                _diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Warning,
                    PartialMergeCodes.GroupRolledBack,
                    "group '" + group.TypeFullName + "' and all its touched files were rolled " +
                    "back; the group stays multi-part and the main converter will pick it " +
                    "up. Resolve the conflict and retry.",
                    TypeFullName: group.TypeFullName));

                foreach (var touched in group.TouchedFiles)
                {
                    var destination = Path.Combine(
                        tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, touched));
                    tree.ResetScratchFile(touched, destination);
                }
            }

            if (iteration == maxIterations - 1)
            {
                CopyInputs(tree);
                success = false;
            }
        }

        // Surface analyzer diagnostics from the final round exactly once; Info
        // lines (CS2J2017) only appear in verbose output.
        if (_lastPlan != null)
        {
            foreach (var diagnostic in _lastPlan.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        // 7. Residual gate: multi-part types must be whitelisted (rollback/skip).
        // Reuses the accepted candidate compilation: the intermediate tree is not
        // touched between its build and this gate (acceptance breaks the loop
        // before restore), so it is the exact accepted content. A round that
        // rewrote nothing accepted the baseline compilation instead of a fresh
        // candidate — byte-identical content, so the gate sees the same source.
        if (_acceptedPlan != null && success && candidate != null)
        {
            foreach (var residual in preprocessor.CheckResidual(candidate, _acceptedPlan))
            {
                _diagnostics.Add(residual);
            }
        }

        // 8. Restore + build/test double verify on the accepted tree. Restore only
        // exists to feed build/test verification (ProjectPassRunner stage 4): when
        // both are off the intermediate tree is never compiled, so restoring the
        // whole solution is pure overhead.
        if (success && !HasErrors)
        {
            if (VerifyBuild || VerifyTests)
            {
                context.Restore();
            }

            var verified = await context.VerifyBuildAndTestsAsync();
            context.Success = verified && !HasErrors;
        }
        else
        {
            context.Success = false;
        }
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("PartialMergePassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new PartialMergeDiagnostic(
            PartialMergeSeverity.Warning,
            string.Empty,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new PartialMergeDiagnostic(
            PartialMergeSeverity.Error,
            PartialMergeCodes.BuildFailed,
            $"dotnet build failed with exit code {exitCode} after partial-merge."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new PartialMergeDiagnostic(
            PartialMergeSeverity.Error,
            PartialMergeCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after partial-merge."));

    public override string? BuildChainSummaryLine()
    {
        var plan = _acceptedPlan ?? _lastPlan;
        if (plan is null)
        {
            return "partial-merge: see verbose diagnostics";
        }

        var s = plan.Statistics;
        var summary = "partial-merge: " + s.GroupsMerged + " group(s) merged, " +
            s.MembersCollapsed + " partial member pair(s) collapsed";
        if (s.PartialDeclarationsRemoved > 0 || s.PartialCallSitesRemoved > 0)
        {
            summary += "; AssumeNoGeneratedPartial removed " +
                s.PartialDeclarationsRemoved + " declaration(s) and " +
                s.PartialCallSitesRemoved + " call site(s)";
        }

        if (s.GroupsRolledBack > 0)
        {
            summary += "; " + s.GroupsRolledBack + " group(s) rolled back";
        }

        return summary;
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != PartialMergeSeverity.Info)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private static List<TypeGroupPlan> AttributeToGroups(Diagnostic error, PartialMergePlan plan)
    {
        var file = error.Location.SourceTree?.FilePath;
        if (file == null)
        {
            return [];
        }

        // Only groups that actually emit file-level edits can introduce errors:
        // nested groups write nothing themselves — their merged text rides the
        // containing group's replacement, so attributing to them double-counts
        // the single containing group and forces spurious whole-tree rollbacks.
        var planSymbols = new HashSet<INamedTypeSymbol>(
            plan.Groups.Select(g => g.Symbol), SymbolEqualityComparer.Default);

        static bool EmitsFileEdits(TypeGroupPlan group, HashSet<INamedTypeSymbol> planSymbols)
        {
            for (var containing = group.Symbol.ContainingType;
                 containing is not null;
                 containing = containing.ContainingType)
            {
                if (planSymbols.Contains(containing))
                {
                    return false;
                }
            }

            return true;
        }

        return plan.ActiveGroups
            .Where(group => EmitsFileEdits(group, planSymbols))
            .Where(group => group.TouchedFiles
                .Any(touched => string.Equals(touched, file, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Build the candidate compilation over the intermediate tree. Files that the
    /// round did not rewrite still hold the original bytes, so their already-parsed
    /// source tree is re-pathed (<see cref="SyntaxTree.WithFilePath"/>) instead of
    /// being read and parsed again; only rewritten files are parsed from disk.
    /// </summary>
    private CSharpCompilation BuildIntermediateCompilation(
        PassTreeContext tree,
        IReadOnlySet<string>? rewrittenSourcePaths = null)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = tree.CsFiles
            .Select(file =>
            {
                var destination = Path.Combine(
                    tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, file));
                if (rewrittenSourcePaths is not null
                    && !rewrittenSourcePaths.Contains(file)
                    && tree.ParsedSyntaxTrees.TryGetValue(file, out var parsed))
                {
                    return parsed.WithFilePath(destination);
                }

                return (SyntaxTree)CSharpSyntaxTree.ParseText(
                    File.ReadAllText(destination), parseOptions, path: destination);
            })
            .ToList();
        trees.AddRange(CreateAdditionalSyntaxTrees(tree));

        return CSharpCompilation.Create(
            Descriptor.Id + "-candidate", trees, GetMetadataReferences(), CreateCompilationOptions());
    }

    /// <summary>
    /// Candidate errors that the ORIGINAL sources did not already report. Only the
    /// trees this round REWROTE are examined: every other tree is byte-identical
    /// to the baseline compilation's source, and the merge only ever unions
    /// members into / moves members between the rewritten declaration files, so a
    /// non-rewritten file's own diagnostics are unchanged — the whole-tree diff
    /// therefore has nothing to add there. This is the repo's established
    /// substitute for a whole-compilation GetDiagnostics (async-normalize
    /// <c>1829abe6</c>, struct-value-semantics <c>aaebe9b5</c>).
    ///
    /// <para>
    /// The candidate's declaration diagnostics are restricted the same way, so a
    /// rewritten file's declaration-level regression (a duplicate member produced
    /// by a member union, say) is still caught even though no tree's SemanticModel
    /// reports it. The per-file baseline side comes from a shared index that binds
    /// the compilation-wide declaration diagnostics once — the same bound the
    /// analyzer's baseline-skip gate already paid — and each file's SemanticModel
    /// diagnostics on first use.
    /// </para>
    ///
    /// <para>
    /// A compilation-level entry-point diagnostic (CS8805) carries a location but
    /// belongs to no tree, so the per-file view cannot classify it; such an error
    /// is re-checked against the lazily bound whole-tree key set, which keeps the
    /// verdict and its order identical to a whole-tree diff.
    /// </para>
    /// </summary>
    private static List<Diagnostic> DiffErrors(
        PassTreeContext tree,
        CSharpCompilation candidate,
        HashSet<string> rewrittenKeys,
        BaselineKeyIndex baseline,
        Func<HashSet<(string File, string Id, string Message)>> fullBaselineKeys)
    {
        List<Diagnostic>? unclassified = null;

        // Declaration diagnostics for the rewritten trees first: they are the
        // compilation-wide stage, and binding them before the per-tree semantic
        // passes keeps the two stages off each other's critical path.
        foreach (var diagnostic in candidate.GetDeclarationDiagnostics())
        {
            Consider(diagnostic);
        }

        foreach (var syntaxTree in candidate.SyntaxTrees)
        {
            if (!rewrittenKeys.Contains(NormalizePath(syntaxTree.FilePath, tree.IntermediateRoot)))
            {
                continue;
            }

            foreach (var diagnostic in candidate.GetSemanticModel(syntaxTree).GetDiagnostics())
            {
                Consider(diagnostic);
            }
        }

        if (unclassified is null)
        {
            return [];
        }

        var full = fullBaselineKeys();
        return unclassified
            .Where(d => !full.Contains((
                NormalizePath(d.Location.SourceTree!.FilePath, tree.IntermediateRoot),
                d.Descriptor.Id,
                d.GetMessage())))
            .ToList();

        void Consider(Diagnostic diagnostic)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error
                || diagnostic.Location.SourceTree is null)
            {
                return;
            }

            var key = NormalizePath(diagnostic.Location.SourceTree.FilePath, tree.IntermediateRoot);
            if (!rewrittenKeys.Contains(key))
            {
                return;
            }

            var sourceTree = baseline.SourceTreeFor(key, tree.SourceRoot);
            if (sourceTree is not null
                && baseline.KeysFor(sourceTree).Contains((diagnostic.Descriptor.Id, diagnostic.GetMessage())))
            {
                return;
            }

            (unclassified ??= []).Add(diagnostic);
        }
    }

    /// <summary>
    /// Per-file baseline error (id, message) keys over the ORIGINAL sources: the
    /// compilation-wide declaration diagnostics, grouped by tree and bound once,
    /// plus each file's own SemanticModel diagnostics, bound on first use. Together
    /// they cover every error the whole-compilation call reports against that tree,
    /// apart from the compilation-level entry-point family (which
    /// <see cref="DiffErrors"/> re-checks against the whole-tree set).
    /// </summary>
    private sealed class BaselineKeyIndex
    {
        private readonly CSharpCompilation _analysis;
        private Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>? _declarationKeysByTree;
        private Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>? _keysByTree;
        private Dictionary<string, SyntaxTree>? _treesByKey;

        public BaselineKeyIndex(CSharpCompilation analysis)
        {
            _analysis = analysis;
        }

        public SyntaxTree? SourceTreeFor(string key, string sourceRoot)
        {
            if (_treesByKey is null)
            {
                var index = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
                foreach (var syntaxTree in _analysis.SyntaxTrees)
                {
                    index.TryAdd(NormalizePath(syntaxTree.FilePath, sourceRoot), syntaxTree);
                }

                _treesByKey = index;
            }

            return _treesByKey.TryGetValue(key, out var tree) ? tree : null;
        }

        public HashSet<(string Id, string Message)> KeysFor(SyntaxTree sourceTree)
        {
            if (_keysByTree is null)
            {
                var declarationKeys = new Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>();
                foreach (var diagnostic in _analysis.GetDeclarationDiagnostics())
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error
                        || diagnostic.Location.SourceTree is null)
                    {
                        continue;
                    }

                    if (!declarationKeys.TryGetValue(diagnostic.Location.SourceTree, out var set))
                    {
                        set = [];
                        declarationKeys[diagnostic.Location.SourceTree] = set;
                    }

                    set.Add((diagnostic.Descriptor.Id, diagnostic.GetMessage()));
                }

                _declarationKeysByTree = declarationKeys;
                _keysByTree = new Dictionary<SyntaxTree, HashSet<(string Id, string Message)>>();
            }

            if (_keysByTree.TryGetValue(sourceTree, out var cached))
            {
                return cached;
            }

            var keys = new HashSet<(string Id, string Message)>(
                _declarationKeysByTree!.TryGetValue(sourceTree, out var declaration)
                    ? declaration
                    : []);
            foreach (var diagnostic in _analysis.GetSemanticModel(sourceTree).GetDiagnostics())
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error
                    && diagnostic.Location.SourceTree is not null)
                {
                    keys.Add((diagnostic.Descriptor.Id, diagnostic.GetMessage()));
                }
            }

            _keysByTree[sourceTree] = keys;
            return keys;
        }
    }

    /// <summary>
    /// Root-relative key for a source-tree path. The baseline is normalized
    /// against the source root and the candidate against the intermediate root,
    /// but the two trees hold the same files at the same relative paths, so both
    /// views agree. A tree not under the given root (the synthesized global-usings
    /// tree) keys on its bare file name so the views still agree.
    /// </summary>
    private static string NormalizePath(string path, string root)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(root), Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? "__external__/" + Path.GetFileName(path)
            : relative.Replace('\\', '/');
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void CopyInputs(PassTreeContext tree)
    {
        foreach (var csFile in tree.CsFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, csFile);
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            tree.ResetScratchFile(csFile, destination);
        }

        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(tree.SourceRoot, asset);
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            tree.ResetScratchFile(asset, destination);
        }
    }
}
