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
        // to confirm candidate errors the cheap per-file baseline could not
        // classify (see DiffErrors). A round that rewrites nothing never asks for
        // it, so the pass stays off the whole-tree GetDiagnostics path entirely.
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
            // active edits. Files with no active edit are already copied.
            var rewriter = new PartialMergeRewriter();
            var rewrittenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var csFile in tree.CsFiles)
            {
                var activePartSpans = plan.ActiveGroups
                    .SelectMany(g => g.Parts)
                    .Where(p => string.Equals(
                        p.SourceFilePath, csFile, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Declaration.FullSpan)
                    .ToHashSet();
                var edits = plan.EditsByFile[csFile]
                    .Where(e => activePartSpans.Contains(e.Span))
                    .ToList();
                if (edits.Count == 0)
                {
                    continue;
                }

                var source = await File.ReadAllTextAsync(csFile);
                var output = rewriter.Apply(source, edits);
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
            var newErrors = DiffErrors(tree, analysisCompilation, candidate, FullBaselineKeys);

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
    /// Candidate errors that the ORIGINAL sources did not already report. The
    /// baseline side is bound PER FILE and only for the files the candidate
    /// reports an error in: an error is "new" exactly when its (id, message) is
    /// absent from the baseline diagnostics of the same file, so no other file's
    /// baseline can change the verdict. That keeps the pass off the whole-tree
    /// GetDiagnostics path for every candidate error a tree's own diagnostics can
    /// account for.
    ///
    /// <para>
    /// A handful of compilation-level diagnostics (the entry-point family, e.g.
    /// CS8805) carry a source location but are not produced by any tree's
    /// SemanticModel, so the per-file view cannot classify them. Those — and any
    /// genuinely new error — are re-checked against the whole-tree key set, which
    /// is therefore bound lazily and only when such an error actually occurs. The
    /// verdict and its order are then exactly the ones a whole-tree diff gives.
    /// </para>
    /// </summary>
    private static List<Diagnostic> DiffErrors(
        PassTreeContext tree,
        CSharpCompilation analysisCompilation,
        CSharpCompilation candidate,
        Func<HashSet<(string File, string Id, string Message)>> fullBaselineKeys)
    {
        Dictionary<string, SyntaxTree>? baselineTreesByKey = null;
        var baselineKeysByFile = new Dictionary<string, HashSet<(string Id, string Message)>>(
            StringComparer.OrdinalIgnoreCase);
        List<Diagnostic>? unclassified = null;

        foreach (var diagnostic in candidate.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error
                || diagnostic.Location.SourceTree is null)
            {
                continue;
            }

            var relative = NormalizePath(diagnostic.Location.SourceTree.FilePath, tree.IntermediateRoot);
            if (!baselineKeysByFile.TryGetValue(relative, out var baselineKeys))
            {
                baselineTreesByKey ??= BuildBaselineTreeIndex(analysisCompilation, tree.SourceRoot);
                baselineKeys = baselineTreesByKey.TryGetValue(relative, out var sourceTree)
                    ? BaselineKeysFor(analysisCompilation, sourceTree)
                    : new HashSet<(string Id, string Message)>();
                baselineKeysByFile[relative] = baselineKeys;
            }

            if (!baselineKeys.Contains((diagnostic.Descriptor.Id, diagnostic.GetMessage())))
            {
                (unclassified ??= []).Add(diagnostic);
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
    }

    /// <summary>
    /// Baseline error (id, message) pairs of one source file: its declaration
    /// diagnostics plus the diagnostics of its SemanticModel. Together they cover
    /// every error the whole-compilation call reports against that tree, apart
    /// from the compilation-level entry-point family (which the lazy whole-tree
    /// fallback in <see cref="DiffErrors"/> re-checks).
    /// </summary>
    private static HashSet<(string Id, string Message)> BaselineKeysFor(
        CSharpCompilation analysisCompilation,
        SyntaxTree sourceTree)
    {
        var keys = new HashSet<(string Id, string Message)>(
            analysisCompilation.GetDeclarationDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error
                    && ReferenceEquals(d.Location.SourceTree, sourceTree))
                .Select(d => (d.Descriptor.Id, d.GetMessage())));

        foreach (var diagnostic in analysisCompilation.GetSemanticModel(sourceTree).GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree is not null)
            {
                keys.Add((diagnostic.Descriptor.Id, diagnostic.GetMessage()));
            }
        }

        return keys;
    }

    /// <summary>
    /// Root-relative path → source tree, for every tree of the analysis
    /// compilation. Both the source root and the intermediate root hold the same
    /// files at the same relative paths, so a candidate tree's key finds its
    /// baseline counterpart here (the synthesized global-usings tree included,
    /// since it carries the same synthetic path in both compilations).
    /// </summary>
    private static Dictionary<string, SyntaxTree> BuildBaselineTreeIndex(
        CSharpCompilation analysisCompilation, string sourceRoot)
    {
        var index = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var syntaxTree in analysisCompilation.SyntaxTrees)
        {
            index.TryAdd(NormalizePath(syntaxTree.FilePath, sourceRoot), syntaxTree);
        }

        return index;
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
