using System.Xml.Linq;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the numeric-boundary pass (C15, M2/M3/M4). Runs on the standard
/// spine: keyword checked/unchecked sites plus project-wide
/// <c>&lt;CheckForOverflowUnderflow&gt;true&lt;/csproj&gt;</c> are rewritten to
/// Cs2jUnsigned.UnsignedMath checked helpers, the shared Cs2jUnsignedMath
/// support library is released into the intermediate tree and ProjectReferences
/// are injected through <see cref="Cs2jUnsignedMathInjection"/>.
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
    private readonly List<string> _rewrittenSourceFiles = new();

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
        _statistics.CheckedOverflowProjectsDetected = _checkForOverflowMap.Values.Count(v => v);
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
            _rewrittenSourceFiles.Add(context.SourceFilePath);
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

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        StripProjectCheckedFlags(tree);

        var checkedHelperSites = _statistics.CheckedBinarySites
            + _statistics.CheckedUnarySites
            + _statistics.CheckedConversionSites
            + _statistics.CheckedIncrementSites
            + _statistics.CompoundAssignments;
        var incrementHelperSites = _statistics.IncrementTargets.UnsignedMathCallSites;
        if (checkedHelperSites == 0 && incrementHelperSites == 0)
        {
            // Pure stripping pass: no support library needed.
            return;
        }

        if (tree.Layout.ProjectFiles.Count == 0)
        {
            // CS2J5309: helper calls were emitted into loose .cs input without a
            // host project; there is nowhere to attach the support library.
            _diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Warning,
                PreprocessDiagnosticCodes.NumericBoundarySingleFileNotSelfContained,
                "numeric-boundary emitted checked-helper calls but the input contains no .csproj/.sln " +
                "host; the Cs2jUnsignedMath support library cannot be attached. Preprocess a project " +
                "or solution directory instead."));
            return;
        }

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(
            tree.Layout, tree.IntermediateRoot);
        if (restoreTarget == null)
        {
            _diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Warning,
                PreprocessDiagnosticCodes.NumericBoundarySingleFileNotSelfContained,
                "numeric-boundary emitted checked-helper calls but no restore target (.csproj/.sln) " +
                "was found in the intermediate tree; the Cs2jUnsignedMath support library cannot be attached."));
            return;
        }

        await Cs2jUnsignedMathInjection.ReleaseAndInjectAsync(
            tree,
            _rewrittenSourceFiles,
            message => _diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Warning,
                PreprocessDiagnosticCodes.NumericBoundaryCheckedSiteInfo,
                message)));
    }

    /// <summary>
    /// Remove <c>&lt;CheckForOverflowUnderflow&gt;</c> from every intermediate
    /// csproj that enabled it. All checked semantics now live in the emitted
    /// helper calls (and in the explicit checked blocks inside the released
    /// support library); keeping the flag would double-check calls and, more
    /// importantly, reject implicit constant-narrowing folds that the pass
    /// materializes with unchecked-wrap values so the intermediate tree compiles.
    /// </summary>
    private void StripProjectCheckedFlags(PassTreeContext tree)
    {
        // The map is keyed by project directory (see
        // NumericBoundaryAnalyzer.ParseCheckForOverflowUnderflow).
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var projectDirectory = Path.GetFullPath(Path.GetDirectoryName(projectFile)!);
            if (!_checkForOverflowMap.TryGetValue(projectDirectory, out var isChecked) || !isChecked)
            {
                continue;
            }

            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot,
                Path.GetRelativePath(tree.SourceRoot, projectFile));
            if (!File.Exists(intermediateProjectFile))
            {
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(intermediateProjectFile, LoadOptions.PreserveWhitespace);
            }
            catch (Exception)
            {
                continue;
            }

            var flags = document.Descendants()
                .Where(e => e.Name.LocalName == "CheckForOverflowUnderflow")
                .ToList();
            if (flags.Count == 0)
            {
                continue;
            }

            foreach (var flag in flags)
            {
                flag.Remove();
            }

            try
            {
                document.Save(intermediateProjectFile, SaveOptions.DisableFormatting);
            }
            catch (Exception)
            {
                // A read-only intermediate file is reported by the build gate;
                // never fail the pass over csproj cleanup.
            }
        }
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
        var line = $"numeric-boundary: {_statistics.FilesScanned} file(s), " +
               $"{_statistics.CheckedBinarySites} binary/" +
               $"{_statistics.CheckedUnarySites} unary/" +
               $"{_statistics.CheckedConversionSites} conversion/" +
               $"{_statistics.CheckedIncrementSites} increment site(s) explicitized, " +
               $"{_statistics.UncheckedExpressionsStripped} unchecked expr(s)/" +
               $"{_statistics.UncheckedBlocksUnwrapped} block(s) stripped, " +
               $"{_statistics.ConstantsMaterialized} constant(s) materialized, " +
               $"{errorCount} error(s)";

        if (_options.EnableDecimalOps)
        {
            var d = _statistics.DecimalOps;
            line += $"; decimal-ops: {d.TotalBinarySites} binary/" +
                    $"{d.TotalComparisonSites} comparison/" +
                    $"{d.TotalUnarySites} unary/" +
                    $"{d.TotalCompoundSites} compound site(s), " +
                    $"{d.TotalPromotionSites} promotion(s), " +
                    $"{d.ConstantsDowngraded} const downgraded, " +
                    $"{d.TotalTemps} temp(s), {d.TotalDeferred} deferred";
        }

        var it = _statistics.IncrementTargets;
        if (it.TotalSites != 0 || it.UnsignedMathCallSites != 0
            || it.PrefixInlineSites != 0 || it.EventSites != 0
            || it.DecimalSites != 0 || it.RefAliasSites != 0
            || it.CustomOperatorSites != 0)
        {
            var tempCount = it.TempsByRole.Values.Sum();
            var deferredCount = it.DeferredByReason.Values.Sum();
            var m2Parts = new List<string>();
            if (it.DecimalSites != 0)
            {
                m2Parts.Add($"{it.DecimalSites} decimal");
            }

            if (it.RefAliasSites != 0)
            {
                m2Parts.Add($"{it.RefAliasSites} ref-alias");
            }

            if (it.CustomOperatorSites != 0)
            {
                m2Parts.Add($"{it.CustomOperatorSites} custom-operator");
            }

            var m2Suffix = m2Parts.Count > 0 ? $", {string.Join("/", m2Parts)} site(s)" : string.Empty;
            line += $"; increment-targets: {it.PropertySites} property/" +
                    $"{it.IndexerSites} indexer/" +
                    $"{it.SpecialArrayElementSites} enum-array/" +
                    $"{it.EnumCastSites + it.UnsignedResidualSites} simple site(s)" +
                    $"{m2Suffix}, " +
                    $"{it.UnsignedMathCallSites} unsigned helper call(s), " +
                    $"{it.PostfixLiftedSites} postfix lifted/" +
                    $"{it.PrefixInlineSites} prefix inlined, " +
                    $"{tempCount} temp(s), {deferredCount} deferred";
        }

        var nl = _statistics.Nullable;
        var nullableDeferred = nl.DeferredByReason.Values.Sum();
        var nullableActive = nl.LiftedBinarySites + nl.LiftedUnarySites
            + nl.LiftedRelationalSites + nl.LiftedEqualitySites
            + nl.LiftedBoolLogicSites + nl.TempsLifted + nullableDeferred;
        if (nullableActive != 0)
        {
            line += $"; nullable-lift: {nl.LiftedBinarySites} arith/" +
                    $"{nl.LiftedUnarySites} unary/" +
                    $"{nl.LiftedRelationalSites} rel/" +
                    $"{nl.LiftedEqualitySites} eq/" +
                    $"{nl.LiftedBoolLogicSites} bool site(s), " +
                    $"{nl.TempsLifted} temp(s), {nullableDeferred} deferred";
        }

        return line;
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

        // ── C34 decimal-ops buckets ──
        var sourceDecimal = source.DecimalOps;
        var targetDecimal = _statistics.DecimalOps;
        MergeCounter(sourceDecimal.BinarySitesByOp, targetDecimal.BinarySitesByOp);
        MergeCounter(sourceDecimal.TempsByRole, targetDecimal.TempsByRole);
        MergeCounter(sourceDecimal.DeferredByReason, targetDecimal.DeferredByReason);
        targetDecimal.EqualityComparisonSites += sourceDecimal.EqualityComparisonSites;
        targetDecimal.RelationalComparisonSites += sourceDecimal.RelationalComparisonSites;
        targetDecimal.UnaryNegateSites += sourceDecimal.UnaryNegateSites;
        targetDecimal.UnaryPlusStrippedSites += sourceDecimal.UnaryPlusStrippedSites;
        targetDecimal.SimpleCompoundSites += sourceDecimal.SimpleCompoundSites;
        targetDecimal.ComplexCompoundSites += sourceDecimal.ComplexCompoundSites;
        targetDecimal.CastPromotionSites += sourceDecimal.CastPromotionSites;
        targetDecimal.ToDecimalUInt32PromotionSites += sourceDecimal.ToDecimalUInt32PromotionSites;
        targetDecimal.ToDecimalUInt64PromotionSites += sourceDecimal.ToDecimalUInt64PromotionSites;
        targetDecimal.ConstantsDowngraded += sourceDecimal.ConstantsDowngraded;
        targetDecimal.ResidualDecimalOperators += sourceDecimal.ResidualDecimalOperators;

        // ── C17 increment-targets buckets ──
        var sourceIncrement = source.IncrementTargets;
        var targetIncrement = _statistics.IncrementTargets;
        targetIncrement.PropertySites += sourceIncrement.PropertySites;
        targetIncrement.IndexerSites += sourceIncrement.IndexerSites;
        targetIncrement.SpecialArrayElementSites += sourceIncrement.SpecialArrayElementSites;
        targetIncrement.EnumCastSites += sourceIncrement.EnumCastSites;
        targetIncrement.EnumFromValueSites += sourceIncrement.EnumFromValueSites;
        targetIncrement.DecimalSites += sourceIncrement.DecimalSites;
        targetIncrement.UnsignedResidualSites += sourceIncrement.UnsignedResidualSites;
        targetIncrement.CustomOperatorSites += sourceIncrement.CustomOperatorSites;
        targetIncrement.RefAliasSites += sourceIncrement.RefAliasSites;
        targetIncrement.UnsignedMathCallSites += sourceIncrement.UnsignedMathCallSites;
        targetIncrement.PrefixInlineSites += sourceIncrement.PrefixInlineSites;
        targetIncrement.PostfixStandaloneSites += sourceIncrement.PostfixStandaloneSites;
        targetIncrement.PostfixLiftedSites += sourceIncrement.PostfixLiftedSites;
        targetIncrement.EventSites += sourceIncrement.EventSites;
        MergeCounter(sourceIncrement.TempsByRole, targetIncrement.TempsByRole);
        MergeCounter(sourceIncrement.DeferredByReason, targetIncrement.DeferredByReason);

        // ── C22 nullable-lift buckets ──
        var sourceNullable = source.Nullable;
        var targetNullable = _statistics.Nullable;
        targetNullable.LiftedBinarySites += sourceNullable.LiftedBinarySites;
        targetNullable.LiftedUnarySites += sourceNullable.LiftedUnarySites;
        targetNullable.LiftedRelationalSites += sourceNullable.LiftedRelationalSites;
        targetNullable.LiftedEqualityNullLiteralSites += sourceNullable.LiftedEqualityNullLiteralSites;
        targetNullable.LiftedEqualityOneSidedSites += sourceNullable.LiftedEqualityOneSidedSites;
        targetNullable.LiftedEqualityBothNullableSites += sourceNullable.LiftedEqualityBothNullableSites;
        targetNullable.LiftedBoolLogicAndSites += sourceNullable.LiftedBoolLogicAndSites;
        targetNullable.LiftedBoolLogicOrSites += sourceNullable.LiftedBoolLogicOrSites;
        targetNullable.LiftedBoolLogicXorSites += sourceNullable.LiftedBoolLogicXorSites;
        targetNullable.LiftedBoolLogicNotSites += sourceNullable.LiftedBoolLogicNotSites;
        targetNullable.GetValueOrDefaultSites += sourceNullable.GetValueOrDefaultSites;
        targetNullable.UnwrapConversions += sourceNullable.UnwrapConversions;
        targetNullable.LiftedConversions += sourceNullable.LiftedConversions;
        targetNullable.LiftedIncrementPrefixSites += sourceNullable.LiftedIncrementPrefixSites;
        targetNullable.LiftedIncrementPostfixSites += sourceNullable.LiftedIncrementPostfixSites;
        targetNullable.LiftedIncrementCompoundSites += sourceNullable.LiftedIncrementCompoundSites;
        targetNullable.NullableEnumBackstopSites += sourceNullable.NullableEnumBackstopSites;
        targetNullable.GuardedCheckedInnerSites += sourceNullable.GuardedCheckedInnerSites;
        targetNullable.TempsLifted += sourceNullable.TempsLifted;
        MergeCounter(sourceNullable.DeferredByReason, targetNullable.DeferredByReason);
    }

    private static void MergeCounter(IReadOnlyDictionary<string, int> source, Dictionary<string, int> target)
    {
        foreach (var pair in source)
        {
            target[pair.Key] = target.GetValueOrDefault(pair.Key) + pair.Value;
        }
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
