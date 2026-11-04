namespace CSharpToJava.Core.RefStructSpanFieldMemoryLift;

/// <summary>单文件 ref-struct-span-field-memory-lift 统计。</summary>
public sealed class RefStructSpanFieldMemoryLiftStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>去掉 ref 修饰符、降级为普通 struct 的声明数。</summary>
    public int RefStructsLowered { get; set; }

    /// <summary>ReadOnlySpan 存储（字段或自动属性后备字段）改造成 ReadOnlyMemory 的数量。</summary>
    public int SpanFieldsMemoryLifted { get; set; }

    /// <summary>含不可安全内存化成员、保留 ref 修饰符的声明数（RSM001）。</summary>
    public int RefStructsPreserved { get; set; }
}
