using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// R12: builds and writes the capability manifest for the final preprocessed
/// tree. Capabilities are derived from final-tree evidence by
/// <see cref="JcsCapabilityScanner"/>; the manifest is deterministic (relative
/// paths only, no timestamps).
/// </summary>
internal static class PreprocessManifestEmitter
{
    public const string ApiManifestFileName = "api-manifest.json";
    public const string ResidualReportFileName = "preprocess-residual-report.json";
    public const string ComplexityLedgerFileName = "preprocess-report.json";

    public static PreprocessManifest Emit(
        string finalTreeRoot,
        string profile,
        IReadOnlyList<PassDescriptor> enabledPasses,
        IReadOnlyList<PassReportEntry> passReports,
        string? residualReportPath,
        string configuration = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(finalTreeRoot);
        ArgumentNullException.ThrowIfNull(enabledPasses);
        ArgumentNullException.ThrowIfNull(passReports);

        var root = Path.GetFullPath(finalTreeRoot);
        var scanPasses = BuildScanPasses(enabledPasses, passReports);

        var variant = ManifestSolutionVariant.FromTree(root, configuration);
        var capabilities = JcsCapabilityScanner.Scan(root, scanPasses, profile);

        var passPlan = enabledPasses
            .Select(pass => new ManifestPassPlanEntry(
                pass.Id,
                pass.Contract is null
                    ? PassContractHash.ComputeMissingContract()
                    : PassContractHash.Compute(pass.Contract)))
            .ToList();

        var contractHash = PassContractHash.ComputeAggregateHashes(
            passPlan.Select(entry => (entry.Id, entry.ContractHash)));

        var manifest = new PreprocessManifest(
            SchemaVersion: PreprocessManifest.ManifestSchemaVersion,
            SubsetVersion: PreprocessManifest.ManifestSubsetVersion,
            Profile: profile,
            SolutionVariant: variant,
            PassPlan: passPlan,
            ContractHash: contractHash,
            InputHash: ManifestInputHash.Compute(root, variant),
            Capabilities: capabilities,
            Artifacts: BuildArtifacts(root, residualReportPath));

        PreprocessManifestWriter.Write(root, manifest);
        return manifest;
    }

    /// <summary>
    /// Projects the executed plan + pass reports onto the scanner-facing
    /// capability passes. Shared by the manifest emitter and the R13 closure
    /// scanner so both read one "enabled and succeeded" fact source.
    /// </summary>
    public static IReadOnlyList<JcsCapabilityScanPass> BuildScanPasses(
        IReadOnlyList<PassDescriptor> enabledPasses,
        IReadOnlyList<PassReportEntry> passReports)
    {
        ArgumentNullException.ThrowIfNull(enabledPasses);
        ArgumentNullException.ThrowIfNull(passReports);

        var reportsById = passReports
            .GroupBy(report => report.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        return enabledPasses
            .Select(pass =>
            {
                reportsById.TryGetValue(pass.Id, out var report);
                return new JcsCapabilityScanPass(
                    pass.Id,
                    pass.Contract,
                    Succeeded: report is null
                        || string.Equals(report.Status, "ok", StringComparison.Ordinal),
                    Diagnostics: report?.Diagnostics ?? Array.Empty<string>());
            })
            .ToList();
    }

    private static ManifestArtifacts BuildArtifacts(
        string finalTreeRoot,
        string? residualReportPath)
    {
        var apiManifest = File.Exists(Path.Combine(finalTreeRoot, ApiManifestFileName))
            ? ApiManifestFileName
            : null;

        var residualReport = residualReportPath is { Length: > 0 }
            ? Path.GetFileName(residualReportPath)
            : ResidualReportFileName;

        return new ManifestArtifacts(
            ApiManifest: apiManifest,
            ResidualReport: residualReport,
            ComplexityLedger: ComplexityLedgerFileName);
    }
}
