namespace CSharpToJava.Core.PointerDerefIndex;

/// <summary>单文件 pointer-deref-index 统计。</summary>
public sealed class PointerDerefIndexStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>指针解引用改写为元素访问的站点数。</summary>
    public int PointerDerefsRewritten { get; set; }

    /// <summary>因无法证明或形态保守而原样保留的指针解引用站点数（PDI001）。</summary>
    public int PointerDerefsPreserved { get; set; }
}
