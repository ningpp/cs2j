namespace CSharpToJava.Core.StackAllocLowering;

/// <summary>单文件 stackalloc-lowering 统计。</summary>
public sealed class StackAllocLoweringStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>Span/ReadOnlySpan 目标 stackalloc 降级数。</summary>
    public int SpanTargetSitesLowered { get; set; }

    /// <summary>裸指针局部 stackalloc 降级数（指针局部声明改为堆数组）。</summary>
    public int PointerLocalSitesLowered { get; set; }

    /// <summary><c>new Span&lt;T&gt;(ptr, n)</c> → <c>new Span&lt;T&gt;(ptr, 0, n)</c> 改写数。</summary>
    public int SpanConstructionsRewritten { get; set; }

    /// <summary>无法安全降级、原文保留的站点数（SL001）。</summary>
    public int RejectedSites { get; set; }

    /// <summary>成功降级的 stackalloc 站点总数。</summary>
    public int StackAllocSitesLowered =>
        SpanTargetSitesLowered + PointerLocalSitesLowered;
}
