using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Family statistics for the <c>expression-desugar</c> pass. File-level
/// counters live here once; each rule contributes its own statistics section
/// (shared <c>__ed_</c> temp numbering is aggregated in <see cref="TempsLifted"/>).
/// M0 only needs the collection-expr site counts to flow.
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

    // ---- Rule sections (append-only as C23/C24/C35 land) ----

    /// <summary>C36 collection-expression section.</summary>
    public CollectionExprStatistics CollectionExpr { get; } = new();

    // Future sections:
    //   public InterpolationStatistics Interpolations { get; } = new();  // C23
    //   public InitializerStatistics   Initializers  { get; } = new();  // C24
    //   public NullConditionalStatistics NullConditional { get; } = ...; // C35

    /// <summary>Merges a per-file result into an accumulating run total.</summary>
    public void MergeFrom(ExpressionDesugarStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        FilesDirtyNoRewrite += other.FilesDirtyNoRewrite;
        ResidualCount += other.ResidualCount;
        TempsLifted += other.TempsLifted;
        CollectionExpr.MergeFrom(other.CollectionExpr);
    }
}
