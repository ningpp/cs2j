using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Family statistics for the <c>expression-desugar</c> pass. File-level
/// counters live here once; each rule contributes its own statistics section
/// (shared <c>__ed_</c> temp numbering is aggregated in <see cref="TempsLifted"/>).
/// </summary>
public sealed class ExpressionDesugarStatistics
{
    // ---- Common file fields (family-wide) ----

    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;

    /// <summary>Residual sites counted by the post-rewrite gate.</summary>
    public int ResidualCount;

    /// <summary>Total temporaries lifted across every rule (shared __ed_ numbering).</summary>
    public int TempsLifted;

    // ---- Rule sections (append-only as rules land) ----

    /// <summary>C23 interpolation-expansion section.</summary>
    public InterpolationStatistics Interpolations { get; } = new();

    /// <summary>C36 collection-expression section.</summary>
    public CollectionExprStatistics CollectionExpr { get; } = new();

    /// <summary>C24 object/collection-initializer section.</summary>
    public InitializerStatistics Initializers { get; } = new();

    /// <summary>C35 null-conditional read-side section.</summary>
    public NullConditionalStatistics NullConditional { get; } = new();

    /// <summary>Merges a per-file result into an accumulating run total.</summary>
    public void MergeFrom(ExpressionDesugarStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        FilesDirtyNoRewrite += other.FilesDirtyNoRewrite;
        ResidualCount += other.ResidualCount;
        TempsLifted += other.TempsLifted;
        Interpolations.MergeFrom(other.Interpolations);
        CollectionExpr.MergeFrom(other.CollectionExpr);
        Initializers.MergeFrom(other.Initializers);
        NullConditional.MergeFrom(other.NullConditional);
    }
}
