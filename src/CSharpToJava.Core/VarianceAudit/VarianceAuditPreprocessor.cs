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
    /// Whole-tree inventory, analysed once and cached (declaration + use sites in
    /// batch 2/M1b; constraints and safety classification in batch 3).
    /// </summary>
    public VarianceAuditAnalysis Analyze()
    {
        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before Analyze.");
        }

        return _analysis ??= BuildAnalysis();
    }

    private VarianceAuditAnalysis BuildAnalysis()
    {
        var analysis = new VarianceSiteInventory(_compilation!, Statistics).Build();
        new UseSiteSafetyClassifier(_compilation!, _options, Statistics).Enrich(analysis);
        new ConstraintAuditor(_compilation!, Statistics).Audit(analysis);
        Statistics.BaselineErrorFilesSkipped = _compilation!.SyntaxTrees.Count(
            tree => VariancePositionFacts.HasBaselineErrors(_compilation.GetSemanticModel(tree)));
        return analysis;
    }

    /// <summary>
    /// Per-file audit (batch 3/M1c): VA001 declaration inventory, VA010/011/015 variance
    /// conversions, VA012 mapped mutability, VA013/014/016 pure flows and VA020-028
    /// constraints. Warnings are always emitted; Info codes only when the option allows.
    /// Files whose baseline tree already has compile errors keep only VA001 (design D12).
    /// </summary>
    public VarianceFileAudit AuditFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var analysis = Analyze();
        var audit = new VarianceFileAudit();
        var path = model.SyntaxTree.FilePath;

        var declarations = analysis.DeclarationSites.Where(site => site.FilePath == path).ToList();
        var uses = analysis.UseSites.Where(site => site.FilePath == path).ToList();
        var constraints = analysis.ConstraintSites.Where(site => site.FilePath == path).ToList();
        var dependents = analysis.ConstraintDependentSites.Where(site => site.FilePath == path).ToList();
        var conversions = analysis.ConversionSites.Where(site => site.FilePath == path).ToList();
        if (declarations.Count == 0 && uses.Count == 0 && constraints.Count == 0
            && dependents.Count == 0 && conversions.Count == 0)
        {
            return audit;
        }

        if (_options.ReportInfo)
        {
            foreach (var declaration in declarations)
            {
                audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                    VarianceSeverity.Info,
                    VarianceCodes.DeclarationSite,
                    DeclarationMessage(declaration),
                    path,
                    declaration.Line,
                    declaration.Column));
            }
        }

        if (VariancePositionFacts.HasBaselineErrors(model))
        {
            return audit;
        }

        foreach (var conversion in conversions)
        {
            audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                VarianceSeverity.Warning, conversion.Code, conversion.Message,
                path, conversion.Line, conversion.Column));
        }

        foreach (var dependent in dependents)
        {
            audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                VarianceSeverity.Warning, dependent.Code, dependent.Message,
                path, dependent.Line, dependent.Column));
        }

        foreach (var constraint in constraints)
        {
            var (code, severity) = ConstraintCode(constraint.Kind);
            if (severity == VarianceSeverity.Info && !_options.ReportInfo)
            {
                continue;
            }

            audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                severity, code, ConstraintMessage(constraint), path, constraint.Line, constraint.Column));
        }

        foreach (var use in uses)
        {
            if (use.VarianceConversion)
            {
                // The conversion edge already emitted VA010/011/015; only the mapped-mutability
                // face (VA012) can still add information here.
                if (use.Classification == VarianceClassification.Unsafe
                    && use.Flow == UseFlow.Producer && use.MutatingMembers is { Count: > 0 })
                {
                    AddMutabilityDiagnostic(audit, use, path);
                }

                continue;
            }

            if (use.MemberResolutionIncomplete)
            {
                audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                    VarianceSeverity.Warning,
                    VarianceCodes.BothConversionSite,
                    $"use site '{use.TypeDisplayName}' has member accesses that could not be resolved "
                    + "(dynamic/error symbol); conservatively classified Both; redesign the boundary",
                    path,
                    use.Line,
                    use.Column));
                continue;
            }

            if (use.Classification == VarianceClassification.Unsafe
                && use.Flow == UseFlow.Producer && use.MutatingMembers is { Count: > 0 })
            {
                AddMutabilityDiagnostic(audit, use, path);
                continue;
            }

            if (!_options.ReportInfo)
            {
                continue;
            }

            var (code, message) = use.Tags.HasFlag(UseTag.NestedComposition)
                ? (VarianceCodes.NestedCompositionSite, NestedMessage(use))
                : use.Flow switch
                {
                    UseFlow.Producer => (VarianceCodes.ProducerUseSite, FlowMessage(use, "Producer", use.SuggestedWildcard)),
                    UseFlow.Consumer => (VarianceCodes.ConsumerUseSite, FlowMessage(use, "Consumer", use.SuggestedWildcard)),
                    _ => (null, null),
                };
            if (code == null)
            {
                continue;
            }

            audit.Diagnostics.Add(new VarianceAuditDiagnostic(
                VarianceSeverity.Info, code, message!, path, use.Line, use.Column));
        }

        return audit;
    }

    private static void AddMutabilityDiagnostic(VarianceFileAudit audit, VarianceUseRecord use, string path)
        => audit.Diagnostics.Add(new VarianceAuditDiagnostic(
            VarianceSeverity.Warning,
            VarianceCodes.MappedMutabilityExpansion,
            $"use site '{use.TypeDisplayName}' is Producer-classified and its Java mapping "
            + $"{use.MappedJavaType ?? "(unmapped metadata variant type)"} exposes mutating member(s) the C# "
            + $"contract does not have: {string.Join(", ", use.MutatingMembers!)}; Java code can write through a "
            + "value C# declared read-only; redesign at the boundary or narrow the mapping",
            path,
            use.Line,
            use.Column));

    private static (string Code, VarianceSeverity Severity) ConstraintCode(string kind) => kind switch
    {
        "struct" => (VarianceCodes.StructConstraint, VarianceSeverity.Warning),
        "unmanaged" => (VarianceCodes.UnmanagedConstraint, VarianceSeverity.Warning),
        "new" => (VarianceCodes.NewConstraint, VarianceSeverity.Info),
        "notnull" => (VarianceCodes.NotNullConstraint, VarianceSeverity.Info),
        "default" => (VarianceCodes.DefaultConstraint, VarianceSeverity.Info),
        _ => (VarianceCodes.ClassConstraint, VarianceSeverity.Info),
    };

    private static string ConstraintMessage(VarianceConstraintSite site) => site.Kind switch
    {
        "struct" => $"type parameter '{site.TypeParameterName}' constrains to 'struct'; Java erases the value-type "
                    + $"constraint and keeps only reference semantics; {site.DependentDefaultSites} default-site(s), "
                    + $"{site.DependentNewSites} construction-site(s), {site.DependentBoxingSites} boxing-site(s), "
                    + $"{site.DependentArraySites} array-site(s), {site.DependentNullableSites} nullable-site(s) depend "
                    + "on value semantics; redesign the constraint or its use sites",
        "unmanaged" => $"type parameter '{site.TypeParameterName}' constrains to 'unmanaged'; this currently emits no "
                       + "diagnostic at all, yet pointer/sizeof/marshalling sites assume an unmanaged layout "
                       + "(cross-ref C06 unsafe-ffm); review before conversion",
        "new" => $"type parameter '{site.TypeParameterName}' declares 'new()'; construction is handled by E10 "
                 + $"(global::System.Activator.CreateInstance<T>()), {site.ActivatorSites} such site(s) found; failure "
                 + "mode is runtime reflection, not compile time",
        "notnull" => $"type parameter '{site.TypeParameterName}' declares 'notnull'; the NRT constraint is compile-time "
                     + "only and is dropped (currently silent)",
        "default" => $"type parameter '{site.TypeParameterName}' declares the 'default' constraint; it is pure override "
                     + "signature metadata and is dropped (currently silent)",
        _ => $"type parameter '{site.TypeParameterName}' declares 'class'; Java types are reference types by default and "
             + "the constraint is dropped (currently silent)",
    };

    private static string DeclarationMessage(VariantDeclarationRecord declaration)
        => $"variance declaration: {ContainerWord(declaration.Container)} "
           + $"'{declaration.TypeName}' parameter '{declaration.TypeParameterName}' is "
           + $"{KindWord(declaration.Kind)}.";

    private static string ContainerWord(VarianceContainer container)
        => container == VarianceContainer.Interface ? "interface" : "delegate";

    private static string KindWord(VarianceKind kind)
        => kind == VarianceKind.Out ? "covariant (out)" : "contravariant (in)";

    private static string FlowMessage(VarianceUseRecord use, string flow, string wildcard)
        => $"use site '{use.TypeDisplayName}' is a pure {flow} (future use-site rewrite candidate: "
           + $"'{wildcard}'); this does not imply the current output is correct.";

    private static string NestedMessage(VarianceUseRecord use)
        => $"nested variance composition at '{use.TypeDisplayName}': "
           + $"{string.Join(" -> ", use.CompositionChain)} => {VariancePositionFacts.FlowWord(use.Flow)}.";

    /// <summary>
    /// Serialize the report (design D13): schema version + pass-present bits + options
    /// + the three site arrays + totals. Paths are relative to the common root of the
    /// recorded files (fallback: the recorded path), no timestamps, so two calls over
    /// the same input are byte-identical.
    /// </summary>
    public string BuildReportJson()
    {
        var analysis = Analyze();
        var root = ComputeCommonRoot(analysis);
        var declarations = analysis.DeclarationSites
            .Select(site => site with { FilePath = Relativize(root, site.FilePath) })
            .ToList();
        var uses = analysis.UseSites
            .Select(site => site with { FilePath = Relativize(root, site.FilePath) })
            .ToList();
        var constraints = analysis.ConstraintSites
            .Select(site => site with { FilePath = Relativize(root, site.FilePath) })
            .ToList();
        declarations.Sort((a, b) => VarianceSiteInventory.Compare(
            a.FilePath, a.Line, a.Column, a.TypeName + "|" + a.TypeParameterName,
            b.FilePath, b.Line, b.Column, b.TypeName + "|" + b.TypeParameterName));
        uses.Sort((a, b) => VarianceSiteInventory.Compare(
            a.FilePath, a.Line, a.Column, a.TypeDisplayName,
            b.FilePath, b.Line, b.Column, b.TypeDisplayName));
        constraints.Sort((a, b) => VarianceSiteInventory.Compare(
            a.FilePath, a.Line, a.Column, a.Kind + "|" + a.TypeParameterName,
            b.FilePath, b.Line, b.Column, b.Kind + "|" + b.TypeParameterName));

        var report = new VarianceAuditReport(
            SchemaVersion: 1,
            PassPresent: new VariancePassPresent(_options.ChainHasGeneric, _options.ChainHasGenericDeepen),
            Options: new VarianceReportOptions(
                _options.Strict, _options.ReportInfo, _options.CheckMappedMutability),
            DeclarationSites: declarations,
            UseSites: uses,
            ConstraintSites: constraints,
            Totals: BuildReportTotals());

        return JsonSerializer.Serialize(report, ReportJsonOptions);
    }

    /// <summary>
    /// VA070 report-written diagnostic (Info). Constructed here so the adapter can append it
    /// after the report is on disk and unit tests can assert the emitted shape without a
    /// full chain run (design §6/D10).
    /// </summary>
    public VarianceAuditDiagnostic BuildReportWrittenDiagnostic(string reportPath)
        => new(
            VarianceSeverity.Info,
            VarianceCodes.ReportWritten,
            $"variance-audit report written: {reportPath} "
            + $"({Statistics.VariantDeclarationsTotal} declaration(s), {Statistics.UseSitesTotal} use site(s), "
            + $"{Statistics.VarianceConversionsTotal} variance conversion(s), {Statistics.ConstraintsTotal} constraint(s)).");

    private static string ComputeCommonRoot(VarianceAuditAnalysis analysis)
    {
        var directories = analysis.DeclarationSites.Select(site => site.FilePath)
            .Concat(analysis.UseSites.Select(site => site.FilePath))
            .Concat(analysis.ConstraintSites.Select(site => site.FilePath))
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!.Replace('\\', '/').Split('/'))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts.Take(parts.Length - 1).ToArray())
            .ToList();
        if (directories.Count == 0)
        {
            return string.Empty;
        }

        var common = directories[0].ToList();
        foreach (var directory in directories.Skip(1))
        {
            var shared = 0;
            while (shared < common.Count && shared < directory.Length
                   && string.Equals(common[shared], directory[shared], StringComparison.Ordinal))
            {
                shared++;
            }

            common = common.Take(shared).ToList();
            if (common.Count == 0)
            {
                break;
            }
        }

        return common.Count == 0 ? string.Empty : string.Join('/', common);
    }

    private static string? Relativize(string root, string? filePath)
    {
        if (filePath == null)
        {
            return null;
        }

        var normalized = filePath.Replace('\\', '/');
        if (root.Length > 0
            && normalized.Length > root.Length
            && normalized[root.Length] == '/'
            && normalized.StartsWith(root, StringComparison.Ordinal))
        {
            return normalized[(root.Length + 1)..];
        }

        return normalized;
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
    IReadOnlyList<VariantDeclarationRecord> DeclarationSites,
    IReadOnlyList<VarianceUseRecord> UseSites,
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