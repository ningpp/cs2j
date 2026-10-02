using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for lexical-normalize (C28). Parse-only standard spine; each file is
/// parsed exactly once inside Core. AfterFilesAsync performs the cross-file
/// duplicate global using audit (LN-5).
/// </summary>
internal sealed class LexicalNormalizePassAdapter : ProjectPassAdapter
{
    private readonly LexicalNormalizeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly List<string> _diagnostics = new();
    private readonly List<(GlobalUsingRecord Record, string RelativePath, int Line)> _globalUsings = new();

    private int _filesTransformed;
    private int _filesClean;
    private int _lineRemoved;
    private int _fileScoped;
    private int _externSites;

    public LexicalNormalizePassAdapter(
        LexicalNormalizeOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.LexicalNormalize);

    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = new LexicalNormalizePreprocessor().Normalize(context.SourceCode, _options);

        foreach (var diagnostic in result.Diagnostics)
        {
            var severity = diagnostic.Severity == LexicalNormalizeSeverity.Error ? "Error"
                : diagnostic.Severity == LexicalNormalizeSeverity.Warning ? "Warning"
                : "Info";
            _diagnostics.Add(
                $"[{severity}] {context.RelativePath}:{diagnostic.Line ?? 0}: {diagnostic.Code}: {diagnostic.Message}");
        }

        _lineRemoved += result.Statistics.LineDirectivesRemoved;
        _fileScoped += result.Statistics.FileScopedNamespacesNormalized;
        _externSites += result.Statistics.ExternAliasDirectives
            + result.Statistics.ExternAliasQualifiedSites;

        foreach (var globalUsing in result.GlobalUsings)
        {
            _globalUsings.Add((globalUsing, context.RelativePath, globalUsing.Line));
        }

        if (result.Changed)
        {
            _filesTransformed++;
            if (_verbose)
            {
                Console.WriteLine($"[lexical-normalize] transformed: {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            });
        }

        _filesClean++;
        if (_verbose)
        {
            Console.WriteLine($"[lexical-normalize] clean (copy): {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (!_options.AuditGlobalUsingDuplicates)
        {
            return Task.CompletedTask;
        }

        foreach (var group in _globalUsings.GroupBy(entry => entry.Record.NormalizedText, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            var locations = string.Join(", ",
                group.Select(g => $"{g.RelativePath}:{g.Line}").Distinct(StringComparer.Ordinal));
            var severity = _options.Strict ? LexicalNormalizeSeverity.Error : LexicalNormalizeSeverity.Warning;

            _diagnostics.Add(
                $"[{(severity == LexicalNormalizeSeverity.Error ? "Error" : "Warning")}] :0: " +
                $"CS2J5521: [LN-5] duplicate global using '{group.Key}' at {locations}");
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] dotnet build failed with exit code {exitCode} after lexical-normalize.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(
            $"[Error] dotnet test failed with exit code {exitCode} after lexical-normalize.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string BuildChainSummaryLine()
        => $"lexical-normalize: {_filesTransformed} transformed, {_filesClean} clean, " +
           $"{_lineRemoved} #line removed, {_externSites} extern-alias site(s), " +
           $"{_globalUsings.Count} global using(s), {_fileScoped} file-scoped normalized";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => verbose
            ? _diagnostics.ToList()
            : _diagnostics.Where(d => !d.StartsWith("[Info]", StringComparison.OrdinalIgnoreCase)).ToList();
}
