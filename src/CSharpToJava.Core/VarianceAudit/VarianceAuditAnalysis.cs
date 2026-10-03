namespace CSharpToJava.Core.VarianceAudit;

/// <summary>Origin of a variant type: declared in the audited source or referenced from metadata (design R10).</summary>
public enum VarianceOrigin
{
    Source,
    Metadata,
}

/// <summary>Container declaring a variant type parameter (design §4.3).</summary>
public enum VarianceContainer
{
    Interface,
    Delegate,
}

/// <summary>in/out direction of a variant type parameter.</summary>
public enum VarianceKind
{
    Out,
    In,
}

/// <summary>Flow classification of one use site (design D2).</summary>
public enum UseFlow
{
    Producer,
    Consumer,
    Both,
}

/// <summary>A variant type-parameter declaration site (out/in on an interface/delegate).</summary>
public sealed record VarianceDeclarationSite(
    VarianceContainer Container,
    string TypeName,
    string TypeParameterName,
    VarianceKind Kind,
    VarianceOrigin Origin,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>A use site of a variant closed type, with its flow classification (design §4.3).</summary>
public sealed record VarianceUseSite(
    UseFlow Flow,
    string MappedTypeName,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>A constraint on a type parameter and its semantic dependency profile (design D5a).</summary>
public sealed record VarianceConstraintSite(
    string Kind,
    string TypeParameterName,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Whole-tree inventory produced by one <see cref="VarianceAuditPreprocessor.Analyze"/>
/// call. Batch 1 (M1a) ships the skeleton with empty lists; batches 2/3 populate them.
/// </summary>
public sealed class VarianceAuditAnalysis
{
    public List<VarianceDeclarationSite> DeclarationSites { get; } = new();
    public List<VarianceUseSite> UseSites { get; } = new();
    public List<VarianceConstraintSite> ConstraintSites { get; } = new();
}