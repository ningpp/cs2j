using CSharpToJava.Core.ConditionalDirectiveStrip;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the conditional-directive strip pass. Text-only; resolves the
/// preprocessor symbol set per file via <see cref="ProjectSymbolResolver"/>.
/// Preserves the string-diagnostic shape ("[Error]"/"[Warning]" lines) of the
/// former ProjectConditionalDirectivePreprocessor facade.
/// </summary>
internal sealed class ConditionalPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly string _configuration;
    private readonly string? _tfm;
    private readonly bool _removeRegions;

    private readonly List<string> _diagnostics = new();
    private int _filesTransformed;
    private int _filesSkippedClean;
    private int _directivesRemoved;
    private int _disabledBranchesRemoved;

    public ConditionalPassAdapter(
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        string configuration,
        string? tfm,
        bool removeRegions)
    {
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _configuration = configuration;
        _tfm = tfm;
        _removeRegions = removeRegions;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Conditional);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    public int FilesTransformed => _filesTransformed;
    public int FilesSkippedClean => _filesSkippedClean;
    public int DirectivesRemoved => _directivesRemoved;
    public int DisabledBranchesRemoved => _disabledBranchesRemoved;
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var symbols = ProjectSymbolResolver.ResolveForFile(
            context.SourceFilePath, _configuration, _tfm);

        foreach (var warning in symbols.Warnings)
        {
            _diagnostics.Add($"[Warning] {context.RelativePath}:0: {warning}");
        }

        var stripOptions = new ConditionalDirectiveStripOptions
        {
            RemoveRegions = _removeRegions,
            Symbols = symbols.Symbols,
        };

        var result = new ConditionalDirectiveStripper().Strip(context.SourceCode, stripOptions);

        foreach (var diagnostic in result.Diagnostics)
        {
            var severity = diagnostic.Severity == ConditionalDirectiveSeverity.Error ? "Error" : "Warning";
            _diagnostics.Add(
                $"[{severity}] {context.RelativePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
        }

        if (result.Changed)
        {
            _filesTransformed++;
            _directivesRemoved += result.Statistics.DirectivesRemoved;
            _disabledBranchesRemoved += result.Statistics.DisabledBranchesRemoved;

            if (_verbose)
            {
                Console.WriteLine(
                    $"[conditional] transformed: {context.RelativePath} " +
                    $"({result.Statistics.DirectivesRemoved} directives, " +
                    $"{result.Statistics.DisabledBranchesRemoved} disabled blocks)");
            }

            return Task.FromResult<PassFileRewrite?>(
                new PassFileRewrite { OutputCode = result.OutputCode, Changed = true });
        }

        _filesSkippedClean++;
        if (_verbose)
        {
            Console.WriteLine($"[conditional] clean (copy): {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add($"[Error] dotnet build failed with exit code {exitCode} after conditional preprocessing.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add($"[Error] dotnet test failed with exit code {exitCode} after conditional preprocessing.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string BuildChainSummaryLine()
        => $"strip-conditional: {_filesTransformed} transformed, " +
           $"{_filesSkippedClean} clean, " +
           $"{_directivesRemoved} directives removed, " +
           $"{_disabledBranchesRemoved} disabled blocks removed";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose) => _diagnostics.ToList();
}
