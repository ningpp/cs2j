using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the numeric-boundary pass (C15, M1). Runs on the standard spine.
/// M1 only strips unchecked shells/blocks, materializes constants and strips
/// non-integer checked shells; checked helper sites never occur yet, so no
/// support library is released in M1 (the guarded injection hook documents the
/// M2 entry point; shared helpers will be extracted from
/// <see cref="UnsignedPassAdapter"/> then, per design §5.1).
/// </summary>
internal sealed class NumericBoundaryPassAdapter : ProjectPassAdapter
{
    private readonly NumericBoundaryOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly NumericBoundaryPreprocessor _rewriter = new();
    private readonly List<NumericBoundaryDiagnostic> _diagnostics = new();
    private readonly NumericBoundaryStatistics _statistics = new();
    private readonly HashSet<string> _reportedCheckedOverflowProjects =
        new(StringComparer.OrdinalIgnoreCase);

    private List<string> _ownReleaseDirectories = new();
    private IReadOnlyDictionary<string, bool> _checkForOverflowMap =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public NumericBoundaryPassAdapter(
        NumericBoundaryOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options with { Strict = strict };
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor
        => PassCatalog.ImplementedById(PassStubCatalog.Ids.NumericBoundary);

    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public NumericBoundaryStatistics Statistics => _statistics;
    public IReadOnlyList<NumericBoundaryDiagnostic> Diagnostics => _diagnostics;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories = FindOwnReleaseLibDirectories(tree.SourceRoot);
        _checkForOverflowMap = NumericBoundaryAnalyzer.ParseCheckForOverflowUnderflow(
            tree.Layout.ProjectFiles.Where(file =>
                file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (IsUnderOwnReleaseLib(context.SourceFilePath, _ownReleaseDirectories))
        {
            // Support-library code legitimately contains checked/unchecked;
            // it never enters the NB-13 gate.
            if (_verbose)
            {
                Console.WriteLine(
                    $"[numeric-boundary] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        var perFileOptions = _options with
        {
            SourceFilePath = context.SourceFilePath,
            CheckForOverflowUnderflowByProjectDirectory = _checkForOverflowMap,
        };

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel, perFileOptions);
        MergeStatistics(result.Statistics);
        ReportProjectCheckedOverflowOnce(context.SourceFilePath);

        foreach (var diagnostic in result.Diagnostics)
        {
            var location = diagnostic.Line > 0
                ? $"({diagnostic.Line},{diagnostic.Column})"
                : string.Empty;
            _diagnostics.Add(diagnostic with
            {
                Message = $"{context.RelativePath}{location}: {diagnostic.Code}: {diagnostic.Message}",
            });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[numeric-boundary] {status}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            var rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // M2 entry point (design §5.1): when CheckedBinarySites/CheckedUnarySites/
        // CheckedConversionSites/CheckedIncrementSites/CompoundAssignments become
        // nonzero, release Cs2jUnsignedMath via UnsignedMathSupportFiles.ReleaseAsync
        // and inject ProjectReferences through shared helpers extracted from
        // UnsignedPassAdapter (no duplicated injection logic). M1 never increments
        // those counters, so this hook is intentionally unreachable.
        var checkedHelperSites = _statistics.CheckedBinarySites
            + _statistics.CheckedUnarySites
            + _statistics.CheckedConversionSites
            + _statistics.CheckedIncrementSites
            + _statistics.CompoundAssignments;
        if (checkedHelperSites > 0)
        {
            throw new InvalidOperationException(
                "numeric-boundary M1 emitted checked-helper site counters, but the support-library " +
                "injection hook lands with M2. This is an implementation error.");
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.NumericBoundaryRestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
            $"dotnet build failed with exit code {exitCode} after numeric-boundary preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.NumericBoundaryTestsFailed,
            $"dotnet test failed with exit code {exitCode} after numeric-boundary preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == NumericBoundarySeverity.Error);

    public override string BuildChainSummaryLine()
    {
        var errorCount = _diagnostics.Count(d => d.Severity == NumericBoundarySeverity.Error);
        return $"numeric-boundary: {_statistics.FilesScanned} file(s), " +
               $"{_statistics.CheckedBinarySites} binary/" +
               $"{_statistics.CheckedUnarySites} unary/" +
               $"{_statistics.CheckedConversionSites} conversion/" +
               $"{_statistics.CheckedIncrementSites} increment site(s) explicitized, " +
               $"{_statistics.UncheckedExpressionsStripped} unchecked expr(s)/" +
               $"{_statistics.UncheckedBlocksUnwrapped} block(s) stripped, " +
               $"{_statistics.ConstantsMaterialized} constant(s) materialized, " +
               $"{errorCount} error(s)";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != NumericBoundarySeverity.Info)
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(NumericBoundaryStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;

        _statistics.CheckedBinarySites += source.CheckedBinarySites;
        _statistics.CheckedUnarySites += source.CheckedUnarySites;
        _statistics.CheckedConversionSites += source.CheckedConversionSites;
        _statistics.CheckedIncrementSites += source.CheckedIncrementSites;
        _statistics.CompoundAssignments += source.CompoundAssignments;

        _statistics.UncheckedExpressionsStripped += source.UncheckedExpressionsStripped;
        _statistics.UncheckedBlocksUnwrapped += source.UncheckedBlocksUnwrapped;
        _statistics.ConstantsMaterialized += source.ConstantsMaterialized;
        _statistics.CheckedNonIntegerShellsStripped += source.CheckedNonIntegerShellsStripped;
        _statistics.CheckedUserOpShellsDeferred += source.CheckedUserOpShellsDeferred;
        _statistics.ResidualUncheckedNodes += source.ResidualUncheckedNodes;
        _statistics.ResidualCheckedNodes += source.ResidualCheckedNodes;

        foreach (var pair in source.HelpersByDomain)
        {
            _statistics.HelpersByDomain[pair.Key] =
                _statistics.HelpersByDomain.GetValueOrDefault(pair.Key) + pair.Value;
        }

        foreach (var pair in source.ErrorsByReason)
        {
            _statistics.ErrorsByReason[pair.Key] =
                _statistics.ErrorsByReason.GetValueOrDefault(pair.Key) + pair.Value;
        }
    }

    /// <summary>CS2J5326 (M1): report each /checked project exactly once.</summary>
    private void ReportProjectCheckedOverflowOnce(string sourceFilePath)
    {
        var projectDirectory = ResolveProjectDirectory(sourceFilePath);
        if (projectDirectory == null
            || _reportedCheckedOverflowProjects.Contains(projectDirectory))
        {
            return;
        }

        _reportedCheckedOverflowProjects.Add(projectDirectory);
        _statistics.CheckedOverflowProjectsDetected++;
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.NumericBoundaryProjectCheckedOverflow,
            $"Project '{projectDirectory}' sets <CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>; " +
            "project-wide checked arithmetic is not supported until numeric-boundary M2."));
    }

    private string? ResolveProjectDirectory(string sourceFilePath)
    {
        if (_checkForOverflowMap.Count == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(sourceFilePath);
        string? best = null;
        foreach (var projectDirectory in _checkForOverflowMap.Keys)
        {
            if (!_checkForOverflowMap[projectDirectory])
            {
                continue;
            }

            var normalized = projectDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((fullPath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(
                        normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(
                        normalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && (best == null || normalized.Length > best.Length))
            {
                best = normalized;
            }
        }

        return best;
    }

    /// <summary>
    /// Collect every previously released cs2j support project in the source tree
    /// (Cs2jUnsignedMath plus, when coexisting in the chain, Cs2jArray/Cs2jRefOutIn).
    /// Identification mirrors each owning adapter: exact reserved directory name
    /// plus content equality with the embedded csproj resource.
    /// </summary>
    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var result = new List<string>();
        result.AddRange(FindContentMatchedDirectories(
            sourceRoot,
            UnsignedMathSupportFiles.LibDirectoryName,
            UnsignedMathSupportFiles.GetProjectFileContent()));
        result.AddRange(FindContentMatchedDirectories(
            sourceRoot,
            RefOutInSupportFiles.LibDirectoryName,
            RefOutInSupportFiles.GetProjectFileContent()));
        result.AddRange(Cs2jArraySupportLibrary.FindOwnReleaseDirectories(sourceRoot));
        return result.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FindContentMatchedDirectories(
        string sourceRoot,
        string directoryName,
        string expectedProjectFileContent)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, directoryName, options))
        {
            var projectFile = Path.Combine(directory, directoryName + ".csproj");
            if (IsOwnReleasedProjectFile(projectFile, expectedProjectFileContent))
            {
                yield return directory;
            }
        }
    }

    private static bool IsOwnReleasedProjectFile(string projectFilePath, string expectedContent)
    {
        try
        {
            if (!File.Exists(projectFilePath))
            {
                return false;
            }

            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(expectedContent),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
