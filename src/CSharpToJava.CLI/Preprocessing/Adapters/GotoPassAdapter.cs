using CSharpToJava.Core.GotoEliminator;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the goto/label elimination pass. Text-only (the eliminator parses
/// each file itself), keeps the "[no-goto]" log prefix and the residual
/// goto/label self-check of the former ProjectGotoPreprocessor facade.
/// </summary>
internal sealed class GotoPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _strict;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly GotoEliminator _eliminator = new();
    private readonly GotoEliminatorOptions _options;
    private readonly List<GotoEliminatorDiagnostic> _diagnostics = new();
    private readonly GotoEliminatorStatistics _statistics = new();

    public GotoPassAdapter(
        bool verbose,
        bool strict,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _verbose = verbose;
        _strict = strict;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _options = new GotoEliminatorOptions(verbose, strict);
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.EliminateGoto);
    public override string LogPrefix => "[no-goto]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    public GotoEliminatorStatistics Statistics => _statistics;
    public IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _eliminator.Eliminate(context.SourceCode, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with
            {
                Message = $"{context.RelativePath}: {diagnostic.Message}",
            });
        }

        var outputCode = result.Changed ? result.OutputCode : context.SourceCode;
        if (ContainsGotoOrLabel(outputCode))
        {
            _diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Error,
                $"{context.RelativePath}: goto/label syntax remains after preprocessing"));
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GotoEliminatorDiagnostic(
            GotoEliminatorSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GotoEliminatorDiagnostic(
            GotoEliminatorSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after goto elimination."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GotoEliminatorDiagnostic(
            GotoEliminatorSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after goto elimination."));

    // Historical behavior: any diagnostic (including warnings) fails the pass.
    public override bool HasErrors => _diagnostics.Count > 0;

    public override string BuildChainSummaryLine()
        => $"eliminate-goto: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.GotosEliminated} goto(s) eliminated";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d => $"[{d.Severity}] {d.Message}").ToList();

    public override string? BuildChainVerbosePrelude(string inputPath, string outputPath)
        => _verbose ? $"No-goto source: {inputPath} -> {outputPath}" : null;

    // goto historically reassigned (not ??) the fingerprint pointers to its own input.
    public override bool RepointsFingerprintToPassInput => true;

    private static bool ContainsGotoOrLabel(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Any(node => node is GotoStatementSyntax or LabeledStatementSyntax);
    }

    private void MergeStatistics(GotoEliminatorStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.MethodsTransformed += source.MethodsTransformed;
        _statistics.MethodsSkipped += source.MethodsSkipped;
        _statistics.GotosEliminated += source.GotosEliminated;
    }
}
