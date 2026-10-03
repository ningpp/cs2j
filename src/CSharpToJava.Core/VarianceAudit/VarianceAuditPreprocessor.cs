using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Public entry point for the C33 variance-audit pass. Read-only by construction:
/// it binds one whole-tree compilation, analyses it once (cached) and produces a
/// machine-readable report. It never emits rewritten source (design D1/D11/D13).
/// </summary>
public sealed class VarianceAuditPreprocessor
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly VarianceAuditOptions _options;
    private CSharpCompilation? _compilation;
    private VarianceAuditAnalysis? _analysis;

    public VarianceAuditPreprocessor(VarianceAuditOptions? options = null)
        => _options = options ?? new VarianceAuditOptions();

    /// <summary>Counters shared by the summary line and the report totals.</summary>
    public VarianceAuditStatistics Statistics { get; } = new();

    public void BindCompilation(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
        _analysis = null;
    }

    /// <summary>
    /// Whole-tree inventory, analysed once and cached. Batch 1 (M1a) returns the
    /// empty skeleton; batches 2/3 fill declaration/use/constraint sites.
    /// </summary>
    public VarianceAuditAnalysis Analyze()
    {
        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before Analyze.");
        }

        return _analysis ??= new VarianceAuditAnalysis();
    }

    /// <summary>Per-file audit; batch 1 reports zero diagnostics (no rewrite logic yet).</summary>
    public VarianceFileAudit AuditFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _ = Analyze();
        return new VarianceFileAudit();
    }

    /// <summary>
    /// Serialize the report (design D13): schema version + pass-present bits + options
    /// + the three site arrays + totals. Paths are caller-supplied relative forms, no
    /// timestamps, so two calls over the same input are byte-identical.
    /// </summary>
    public string BuildReportJson()
    {
        var analysis = Analyze();
        var report = new VarianceAuditReport(
            SchemaVersion: 1,
            PassPresent: new VariancePassPresent(_options.ChainHasGeneric, _options.ChainHasGenericDeepen),
            Options: new VarianceReportOptions(
                _options.Strict, _options.ReportInfo, _options.CheckMappedMutability),
            DeclarationSites: analysis.DeclarationSites,
            UseSites: analysis.UseSites,
            ConstraintSites: analysis.ConstraintSites,
            Totals: BuildReportTotals());

        return JsonSerializer.Serialize(report, ReportJsonOptions);
    }

    private VarianceReportTotals BuildReportTotals()
        => new(
            FilesScanned: Statistics.FilesScanned,
            FilesSkippedClean: Statistics.FilesSkippedClean,
            VariantDeclarationsOutInterface: Statistics.VariantDeclarationsOutInterface,
            VariantDeclarationsInInterface: Statistics.VariantDeclarationsInInterface,
            VariantDeclarationsOutDelegate: Statistics.VariantDeclarationsOutDelegate,
            VariantDeclarationsInDelegate: Statistics.VariantDeclarationsInDelegate,
            MetadataVariantTypesUsed: Statistics.MetadataVariantTypesUsed,
            UseSitesTotal: Statistics.UseSitesTotal,
            UseSitesProducer: Statistics.UseSitesProducer,
            UseSitesConsumer: Statistics.UseSitesConsumer,
            UseSitesBoth: Statistics.UseSitesBoth,
            VarianceConversionsCovariant: Statistics.VarianceConversionsCovariant,
            VarianceConversionsContravariant: Statistics.VarianceConversionsContravariant,
            VarianceConversionsArray: Statistics.VarianceConversionsArray,
            NestedCompositionSites: Statistics.NestedCompositionSites,
            UnsafeUseSites: Statistics.UnsafeUseSites,
            ConstraintsStruct: Statistics.ConstraintsStruct,
            ConstraintsUnmanaged: Statistics.ConstraintsUnmanaged,
            ConstraintsNew: Statistics.ConstraintsNew,
            ConstraintsNotNull: Statistics.ConstraintsNotNull,
            ConstraintsDefault: Statistics.ConstraintsDefault,
            ConstraintsClass: Statistics.ConstraintsClass,
            ConstraintDependentDefaultSites: Statistics.ConstraintDependentDefaultSites,
            ConstraintDependentNewSites: Statistics.ConstraintDependentNewSites,
            ConstraintDependentBoxingSites: Statistics.ConstraintDependentBoxingSites,
            ConstraintDependentArraySites: Statistics.ConstraintDependentArraySites,
            ConstraintDependentNullableSites: Statistics.ConstraintDependentNullableSites,
            BaselineErrorFilesSkipped: Statistics.BaselineErrorFilesSkipped,
            ReportsWritten: Statistics.ReportsWritten);
}

internal sealed record VarianceAuditReport(
    int SchemaVersion,
    VariancePassPresent PassPresent,
    VarianceReportOptions Options,
    IReadOnlyList<VarianceDeclarationSite> DeclarationSites,
    IReadOnlyList<VarianceUseSite> UseSites,
    IReadOnlyList<VarianceConstraintSite> ConstraintSites,
    VarianceReportTotals Totals);

internal sealed record VariancePassPresent(bool Generic, bool GenericDeepen);

internal sealed record VarianceReportOptions(bool Strict, bool ReportInfo, bool CheckMappedMutability);

internal sealed record VarianceReportTotals(
    int FilesScanned,
    int FilesSkippedClean,
    int VariantDeclarationsOutInterface,
    int VariantDeclarationsInInterface,
    int VariantDeclarationsOutDelegate,
    int VariantDeclarationsInDelegate,
    int MetadataVariantTypesUsed,
    int UseSitesTotal,
    int UseSitesProducer,
    int UseSitesConsumer,
    int UseSitesBoth,
    int VarianceConversionsCovariant,
    int VarianceConversionsContravariant,
    int VarianceConversionsArray,
    int NestedCompositionSites,
    int UnsafeUseSites,
    int ConstraintsStruct,
    int ConstraintsUnmanaged,
    int ConstraintsNew,
    int ConstraintsNotNull,
    int ConstraintsDefault,
    int ConstraintsClass,
    int ConstraintDependentDefaultSites,
    int ConstraintDependentNewSites,
    int ConstraintDependentBoxingSites,
    int ConstraintDependentArraySites,
    int ConstraintDependentNullableSites,
    int BaselineErrorFilesSkipped,
    int ReportsWritten);