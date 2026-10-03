using System.Text;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the extension-methods pass (C19): whole-tree bind+analyze once,
/// per-file rewrite, post-rewrite residual scan. Structurally close to
/// <see cref="GenericDeepenPassAdapter"/> but with NO generated-file
/// distribution (no holders, no new files). M0: rules are empty, so every file
/// is byte-copied (Changed=false; BOM preserved) and the intermediate tree is
/// identical to the input.
/// </summary>
internal sealed class ExtensionMethodResolvePassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-extension-methods-src";

    private readonly ExtensionMethodResolveOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly ExtensionMethodResolvePreprocessor _preprocessor = new();
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionMethodResolveStatistics _statistics = new();

    public ExtensionMethodResolvePassAdapter(
        ExtensionMethodResolveOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ExtensionMethods);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    /// <summary>C19 explicitly parses at the latest language version (design §4.1; C#14 block probe relies on it).</summary>
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated GlobalUsings.g.cs;
    /// the one-shot binding compilation must mirror them, otherwise extension
    /// lookups whose receivers live in System.Collections.Generic / System.Linq
    /// fail overload resolution and the sites are falsely reported unbound.
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

        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "__cs2j_global_usings.g.cs"),
        };
    }

    public ExtensionMethodResolveStatistics Statistics => _statistics;
    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_options);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(context.SemanticModel!);
        _statistics.Merge(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine($"[extension-methods] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        RunResidualScan(tree);
        return Task.CompletedTask;
    }

    /// <summary>Post-rewrite gate (design §4.5): recompile the intermediate tree and count leftover shapes.</summary>
    private void RunResidualScan(PassTreeContext tree)
    {
        if (!_options.RunResidualScan)
        {
            return;
        }

        var parseOptions = CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file));
        }

        // Mirror the SDK implicit global usings for gate binding as well.
        trees.AddRange(CreateAdditionalSyntaxTrees(tree));

        if (trees.Count == 0)
        {
            return;
        }

        var compilation = CSharpCompilation.Create(
            CompilationName ?? "cs2j-extension-methods-residual",
            trees, GetMetadataReferences(), CreateCompilationOptions());
        foreach (var (rule, count) in _preprocessor.ResidualScan(compilation))
        {
            _statistics.ResidualsByRule.TryGetValue(rule, out var existing);
            _statistics.ResidualsByRule[rule] = existing + count;
            _diagnostics.Add(new ExtensionDiagnostic(
                _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning,
                ExtensionRule.Residual,
                ExtensionMethodCodes.ResidualFound,
                $"{count} residual site(s) of rule '{rule}' remain after extension-methods; the rewrite did not cover them (design §4.5).",
                tree.IntermediateRoot));
        }
    }

    // Design §6: CS2J1230 is Error for both restore and build failures; CS2J1231 for test failures.

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after extension-methods preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after extension-methods preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ExtensionSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ExtensionSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
