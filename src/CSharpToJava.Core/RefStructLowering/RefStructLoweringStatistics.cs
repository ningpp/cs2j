namespace CSharpToJava.Core.RefStructLowering;

/// <summary>单文件 ref-struct-lowering 统计。</summary>
public sealed class RefStructLoweringStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>去掉 ref 修饰符、降级为普通 struct 的声明数。</summary>
    public int RefStructsLowered { get; set; }

    /// <summary>含 ref-only 成员、保留 ref 修饰符的声明数（RSL001）。</summary>
    public int RefStructsPreserved { get; set; }
}
