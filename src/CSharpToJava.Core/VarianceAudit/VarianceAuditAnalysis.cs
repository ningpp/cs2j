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

/// <summary>Cross-cutting labels attached to a use site (design D2).</summary>
[Flags]
public enum UseTag
{
    None = 0,

    /// <summary>The site depends on a C# variance implicit conversion (batch 3, D3a).</summary>
    VarianceConversion = 1,

    /// <summary>Nested generic variance composition; see <c>CompositionChain</c> (design D7).</summary>
    NestedComposition = 2,
}

/// <summary>Three-state safety conclusion of one use site (design §4.4/D2).</summary>
public enum VarianceClassification
{
    /// <summary>Direction matches the declaration variance and no conversion/mutability issue.</summary>
    Safe,

    /// <summary>Depends on a variance implicit conversion; needs a wildcard the current emission lacks.</summary>
    NeedsWildcard,

    /// <summary>Both-flow conversion, unresolved candidate, or mapped-mutability expansion.</summary>
    Unsafe,
}

/// <summary>Kind of a constraint clause recognised by the constraint auditor (design D5a).</summary>
public enum ConstraintKind
{
    Struct,
    Unmanaged,
    New,
    NotNull,
    Default,
    Class,
}

/// <summary>A constraint on a type parameter and its semantic dependency profile (design D5a/D5b).</summary>
public sealed record VarianceConstraintSite(
    string Kind,
    string TypeParameterName,
    string DeclaringName,
    int DependentDefaultSites,
    int DependentNewSites,
    int DependentBoxingSites,
    int DependentArraySites,
    int DependentNullableSites,
    int ActivatorSites,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>A per-site diagnostic target discovered while auditing a constraint (VA023/VA027, design D5b).</summary>
public sealed record VarianceConstraintDependentSite(
    string Code,
    string Message,
    string TypeParameterName,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Whole-tree inventory produced by one <see cref="VarianceAuditPreprocessor.Analyze"/>
/// call. Batch 1 (M1a) shipped the empty skeleton; batch 2 populates declaration and
/// use sites; batch 3 fills constraints, safety classification and variance conversions.
/// </summary>
public sealed class VarianceAuditAnalysis
{
    public List<VariantDeclarationRecord> DeclarationSites { get; } = new();
    public List<VarianceUseRecord> UseSites { get; } = new();
    public List<VarianceConstraintSite> ConstraintSites { get; } = new();
    public List<VarianceConstraintDependentSite> ConstraintDependentSites { get; } = new();
    public List<VarianceConversionFinding> ConversionSites { get; } = new();
}