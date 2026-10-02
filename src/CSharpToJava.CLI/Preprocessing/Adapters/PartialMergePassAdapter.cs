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

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;

        // 1. Explicit 1:1 copy; baseline error set reads ORIGINAL content at dest paths.
        CopyInputs(tree);
        var baseline = BuildErrorSet(tree);

        var preprocessor = new PartialMergePreprocessor(_options);
        var excludedGroups = new HashSet<string>(StringComparer.Ordinal);
        PartialMergePlan? acceptedPlan = null;
        PartialMergePlan? lastPlan = null;
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 2. Every round starts from originals (replay, like JavaKeywords L101-103).
            CopyInputs(tree);

            var analysisCompilation = context.BuildStandardCompilation();
            var plan = preprocessor.Analyze(analysisCompilation, tree.SourceRoot);
            lastPlan = plan;
            foreach (var group in plan.Groups
                         .Where(g => excludedGroups.Contains(g.TypeFullName)))
            {
                group.RolledBack = true;
            }

            // 3. Per-file rewrites; filter at edit granularity (part spans), so a
            // file shared by an active and a rolled-back group only replays the
            // active edits. Files with no active edit are already copied.
            var rewriter = new PartialMergeRewriter();
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
            }

            // 4. Candidate recompile + baseline diff.
            var candidate = BuildIntermediateCompilation(tree);
            var newErrors = DiffErrors(baseline, candidate, tree.IntermediateRoot);

            if (newErrors.Count == 0)
            {
                acceptedPlan = plan;
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
                    File.Copy(touched, destination, overwrite: true);
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
        if (lastPlan != null)
        {
            foreach (var diagnostic in lastPlan.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        // 7. Residual gate: multi-part types must be whitelisted (rollback/skip).
        if (acceptedPlan != null && success)
        {
            var finalTree = BuildIntermediateCompilation(tree);
            foreach (var residual in preprocessor.CheckResidual(finalTree, acceptedPlan))
            {
                _diagnostics.Add(residual);
            }
        }

        // 8. Restore + build/test double verify on the accepted tree.
        if (success && !HasErrors)
        {
            context.Restore();
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
        => "partial-merge: see verbose diagnostics";

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

        return plan.ActiveGroups
            .Where(group => group.TouchedFiles
                .Any(touched => string.Equals(touched, file, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private CSharpCompilation BuildIntermediateCompilation(PassTreeContext tree)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = tree.CsFiles
            .Select(file =>
            {
                var destination = Path.Combine(
                    tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, file));
                return CSharpSyntaxTree.ParseText(
                    File.ReadAllText(destination), parseOptions, path: destination);
            })
            .ToList();
        trees.AddRange(CreateAdditionalSyntaxTrees(tree));

        return CSharpCompilation.Create(
            Descriptor.Id + "-candidate", trees, GetMetadataReferences(), CreateCompilationOptions());
    }

    /// <summary>(relative intermediate path, error id, message) set over ORIGINAL sources.</summary>
    private HashSet<(string File, string Id, string Message)> BuildErrorSet(PassTreeContext tree)
        => new(BuildIntermediateCompilation(tree)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null)
            .Select(d => (
                NormalizePath(d.Location.SourceTree!.FilePath, tree.IntermediateRoot),
                d.Descriptor.Id,
                d.GetMessage())));

    private static List<Diagnostic> DiffErrors(
        HashSet<(string File, string Id, string Message)> baseline,
        CSharpCompilation candidate,
        string intermediateRoot)
        => candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null
                && !baseline.Contains((
                    NormalizePath(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())))
            .ToList();

    private static string NormalizePath(string path, string intermediateRoot)
        => Path.GetRelativePath(intermediateRoot, Path.GetFullPath(path)).Replace('\\', '/');

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
            File.Copy(csFile, destination, overwrite: true);
        }

        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(tree.SourceRoot, asset);
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset, destination, overwrite: true);
        }
    }
}
