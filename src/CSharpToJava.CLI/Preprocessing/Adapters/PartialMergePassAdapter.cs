using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for partial-merge (C20). Drives its own whole-tree execution like
/// JavaKeywordsPassAdapter (design §5): prepare/copy → whole-tree semantic
/// analysis → per-file deterministic rewrites → restore → build/test verify.
/// M1 has no rollback replay (M2 adds baseline/diff/rollback/residual).
/// </summary>
internal sealed class PartialMergePassAdapter : ProjectPassAdapter
{
    private readonly PartialMergeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly List<string> _diagnostics = new();

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

        // 1. Explicit 1:1 copy of .cs inputs and non-.cs assets (mirror
        // JavaKeywords L56-85: intermediate input graph can be empty).
        CopyInputs(tree);

        // 2. Whole-tree semantic compilation and analysis.
        var compilation = context.BuildStandardCompilation();
        var preprocessor = new PartialMergePreprocessor(_options);
        var plan = preprocessor.Analyze(compilation, tree.SourceRoot);

        foreach (var diagnostic in plan.Diagnostics)
        {
            _diagnostics.Add(diagnostic.ToMsBuildString());
        }

        // 3. Per-file rewrite (edits) or verbatim copy (no edits).
        var rewriter = new PartialMergeRewriter();
        foreach (var sourceFile in tree.SourceFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, sourceFile).Replace('\\', '/');
            var edits = plan.EditsByFile[sourceFile].ToList();
            if (edits.Count == 0)
            {
                context.CopyVerbatim(sourceFile, relativePath);
                continue;
            }

            var source = await File.ReadAllTextAsync(sourceFile);
            var output = rewriter.Apply(source, edits);
            var fileContext = new PassFileContext
            {
                SourceCode = source,
                SourceFilePath = sourceFile,
                RelativePath = relativePath,
                DestinationFilePath = Path.Combine(tree.IntermediateRoot, relativePath),
            };
            await context.WriteRewrittenAsync(fileContext, new PassFileRewrite
            {
                OutputCode = output,
                Changed = true,
                PreserveBom = true,
            });

            if (_verbose)
            {
                Console.WriteLine($"[partial-merge] transformed: {relativePath}");
            }
        }

        // M1: no candidate recompile / rollback replay — added in Task 13.

        // 4. Restore + build/test double verify (descriptor defaults both on).
        context.Restore();
        var verified = await context.VerifyBuildAndTestsAsync();
        context.Success = verified && !HasErrors;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("PartialMergePassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] {PartialMergeCodes.BuildFailed}: dotnet build failed with exit code {exitCode} after partial-merge.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] {PartialMergeCodes.TestsFailed}: dotnet test failed with exit code {exitCode} after partial-merge.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.Contains("error CS2J", StringComparison.OrdinalIgnoreCase)
                                 || d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string? BuildChainSummaryLine()
        => "partial-merge: see verbose diagnostics";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => verbose ? _diagnostics.ToList() : _diagnostics;

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
