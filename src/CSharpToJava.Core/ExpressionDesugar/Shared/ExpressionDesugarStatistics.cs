using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;

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

    // ---- Rule sections (append-only as C24/C35 land) ----

    /// <summary>C23 interpolation-expansion section.</summary>
    public InterpolationStatistics Interpolations { get; } = new();

    /// <summary>C36 collection-expression section.</summary>
    public CollectionExprStatistics CollectionExpr { get; } = new();

    // Future sections:
    //   public InitializerStatistics   Initializers  { get; init; } = new();  // C24
    //   public NullConditionalStatistics NullConditional { get; } = ...;       // C35

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
    }
}
