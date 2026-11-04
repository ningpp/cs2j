using CSharpToJava.Core.CompatMethodReplacement;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>compat-method-replacement</c> Expression pass.
/// It uses the standard <see cref="ProjectPassAdapter"/> spine (semantic model
/// required) and rewrites each file through <see cref="CompatMethodReplacementPreprocessor"/>:
/// the registered known method's body is replaced with its managed
/// implementation (signature/attributes/modifiers preserved), every other
/// method stays verbatim. There is no support library and no generated file.
/// </summary>
internal sealed class CompatMethodReplacementPassAdapter : ProjectPassAdapter
{
    private readonly CompatMethodReplacementOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly CompatMethodReplacementPreprocessor _preprocessor;
    private readonly List<CompatMethodReplacementDiagnostic> _diagnostics = new();
    private readonly CompatMethodReplacementStatistics _statistics = new();

    public CompatMethodReplacementPassAdapter(
        CompatMethodReplacementOptions options,
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
        _preprocessor = new CompatMethodReplacementPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.CompatMethodReplacement);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "compat-method-replacement-preprocess";

    public CompatMethodReplacementStatistics Statistics => _statistics;
    public IReadOnlyList<CompatMethodReplacementDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!);
        _statistics.MergeFrom(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[compat-method-replacement] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                context.RelativePath);
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // "Entry not found": a registered known method exists in the table but
        // never matched any declaration in the processed file set.
        if (_options.ReportUnusedEntries)
        {
            foreach (var entry in _options.Registry.Entries)
            {
                if (_statistics.MatchedEntryKeys.Contains(entry.Key))
                    continue;

                _statistics.EntriesUnused++;
                _diagnostics.Add(new CompatMethodReplacementDiagnostic(
                    CompatMethodReplacementSeverity.Warning,
                    CompatMethodReplacementDiagnosticCode.CMR001,
                    $"compat method replacement entry '{entry.Key}' matched no declaration " +
                    "in the processed file set."));
            }
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new CompatMethodReplacementDiagnostic(
            CompatMethodReplacementSeverity.Warning,
            CompatMethodReplacementDiagnosticCode.CMR010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new CompatMethodReplacementDiagnostic(
            CompatMethodReplacementSeverity.Error,
            CompatMethodReplacementDiagnosticCode.CMR010,
            $"dotnet build failed with exit code {exitCode} after compat-method-replacement " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new CompatMethodReplacementDiagnostic(
            CompatMethodReplacementSeverity.Error,
            CompatMethodReplacementDiagnosticCode.CMR010,
            $"dotnet test failed with exit code {exitCode} after compat-method-replacement " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == CompatMethodReplacementSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"compat-method-replacement: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.MethodsReplaced} method(s) replaced, " +
           $"{_statistics.MethodsAlreadyReplaced} already replaced, " +
           $"{_statistics.SignatureMismatches} signature mismatch(es) (CMR001), " +
           $"{_statistics.EntriesUnused} unused table entry(ies)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != CompatMethodReplacementSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();
}
