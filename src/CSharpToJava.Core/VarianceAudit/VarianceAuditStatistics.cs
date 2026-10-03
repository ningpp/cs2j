namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Counters for the variance-audit pass (design §4.5). Buckets stay flat so the
/// report totals and the CLI summary line read from the same source of truth.
/// </summary>
public sealed class VarianceAuditStatistics
{
    public int FilesScanned;
    public int FilesSkippedClean;

    // Variant declarations by variance × container.
    public int VariantDeclarationsOutInterface;
    public int VariantDeclarationsInInterface;
    public int VariantDeclarationsOutDelegate;
    public int VariantDeclarationsInDelegate;

    /// <summary>Distinct BCL metadata variant types encountered at use sites.</summary>
    public int MetadataVariantTypesUsed;

    public int UseSitesTotal;
    public int UseSitesProducer;
    public int UseSitesConsumer;
    public int UseSitesBoth;

    public int VarianceConversionsCovariant;
    public int VarianceConversionsContravariant;
    public int VarianceConversionsArray;

    public int NestedCompositionSites;
    public int UnsafeUseSites;

    // Constraints by kind.
    public int ConstraintsStruct;
    public int ConstraintsUnmanaged;
    public int ConstraintsNew;
    public int ConstraintsNotNull;
    public int ConstraintsDefault;
    public int ConstraintsClass;

    // Sites whose semantics depend on a struct/unmanaged constraint (design D5b).
    public int ConstraintDependentDefaultSites;
    public int ConstraintDependentNewSites;
    public int ConstraintDependentBoxingSites;
    public int ConstraintDependentArraySites;
    public int ConstraintDependentNullableSites;

    public int BaselineErrorFilesSkipped;
    public int ReportsWritten;

    public int VariantDeclarationsTotal
        => VariantDeclarationsOutInterface + VariantDeclarationsInInterface
           + VariantDeclarationsOutDelegate + VariantDeclarationsInDelegate;

    public int VarianceConversionsTotal
        => VarianceConversionsCovariant + VarianceConversionsContravariant + VarianceConversionsArray;

    public int ConstraintsTotal
        => ConstraintsStruct + ConstraintsUnmanaged + ConstraintsNew
           + ConstraintsNotNull + ConstraintsDefault + ConstraintsClass;

    public void Merge(VarianceAuditStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesSkippedClean += source.FilesSkippedClean;
        VariantDeclarationsOutInterface += source.VariantDeclarationsOutInterface;
        VariantDeclarationsInInterface += source.VariantDeclarationsInInterface;
        VariantDeclarationsOutDelegate += source.VariantDeclarationsOutDelegate;
        VariantDeclarationsInDelegate += source.VariantDeclarationsInDelegate;
        MetadataVariantTypesUsed += source.MetadataVariantTypesUsed;
        UseSitesTotal += source.UseSitesTotal;
        UseSitesProducer += source.UseSitesProducer;
        UseSitesConsumer += source.UseSitesConsumer;
        UseSitesBoth += source.UseSitesBoth;
        VarianceConversionsCovariant += source.VarianceConversionsCovariant;
        VarianceConversionsContravariant += source.VarianceConversionsContravariant;
        VarianceConversionsArray += source.VarianceConversionsArray;
        NestedCompositionSites += source.NestedCompositionSites;
        UnsafeUseSites += source.UnsafeUseSites;
        ConstraintsStruct += source.ConstraintsStruct;
        ConstraintsUnmanaged += source.ConstraintsUnmanaged;
        ConstraintsNew += source.ConstraintsNew;
        ConstraintsNotNull += source.ConstraintsNotNull;
        ConstraintsDefault += source.ConstraintsDefault;
        ConstraintsClass += source.ConstraintsClass;
        ConstraintDependentDefaultSites += source.ConstraintDependentDefaultSites;
        ConstraintDependentNewSites += source.ConstraintDependentNewSites;
        ConstraintDependentBoxingSites += source.ConstraintDependentBoxingSites;
        ConstraintDependentArraySites += source.ConstraintDependentArraySites;
        ConstraintDependentNullableSites += source.ConstraintDependentNullableSites;
        BaselineErrorFilesSkipped += source.BaselineErrorFilesSkipped;
        ReportsWritten += source.ReportsWritten;
    }

    public string SummaryLine()
        => $"variance-audit: {VariantDeclarationsTotal} variant declaration(s), " +
           $"{UseSitesTotal} use site(s) ({UseSitesProducer}/{UseSitesConsumer}/{UseSitesBoth}), " +
           $"{VarianceConversionsTotal} variance conversion(s), {UnsafeUseSites} unsafe; " +
           $"{ConstraintsTotal} constraints (struct/unmanaged/new/...); " +
           "report=variance-audit-report.json";
}