namespace CSharpToJava.Core.PointerDiffInline;

/// <summary>Aggregated statistics for the <c>pointer-diff-inline</c> pass.</summary>
public sealed class PointerDiffInlineStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>Pointer-difference helper declarations inlined and then removed.</summary>
    public int PointerDiffMethodsInlined { get; set; }

    /// <summary>
    /// Pointer-difference helper declarations removed because the whole
    /// compilation contains no reference to them, even though this pass inlined
    /// no call site (for example when an earlier pass replaced the only caller).
    /// </summary>
    public int PointerDiffMethodsRemovedUnused { get; set; }

    /// <summary>Helper invocation call sites rewritten to <c>(R)(a - b)</c>.</summary>
    public int PointerDiffCallSitesInlined { get; set; }

    /// <summary>
    /// Two-pointer/integral helpers that stayed in the output (body shape was not
    /// a provable same-type pointer difference, or the declaration still has
    /// references / is not private static).
    /// </summary>
    public int PointerDiffMethodsPreserved { get; set; }
}
