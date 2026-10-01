namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 统计计数器（设计 §4「按规则命中数、拒绝数、residual 数」）。
/// M0 仅 unsafe 段有账：unsafe 块剥除数与各位置 unsafe 修饰符剥除数；
/// 拒绝/residual/隐式 stackalloc 等计数器为后续里程碑预留。
/// </summary>
public sealed class UnsafeFfmStatistics
{
    // ---- 文件级 ----

    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    // ---- R1：unsafe 上下文剥除 ----

    /// <summary>unsafe 块（UnsafeStatementSyntax）剥除数。</summary>
    public int UnsafeBlocksStripped { get; set; }

    /// <summary>class/struct/interface/record 声明 unsafe 修饰符剥除数。</summary>
    public int UnsafeTypeModifiersStripped { get; set; }

    /// <summary>方法声明 unsafe 修饰符剥除数。</summary>
    public int UnsafeMethodModifiersStripped { get; set; }

    /// <summary>字段（含 event field）声明 unsafe 修饰符剥除数。</summary>
    public int UnsafeFieldModifiersStripped { get; set; }

    /// <summary>构造函数声明 unsafe 修饰符剥除数。</summary>
    public int UnsafeConstructorModifiersStripped { get; set; }

    /// <summary>局部函数（local function）声明 unsafe 修饰符剥除数。</summary>
    public int UnsafeLocalFunctionModifiersStripped { get; set; }

    /// <summary>
    /// 其它声明 unsafe 修饰符剥除数：终结器、运算符、转换运算符、属性、索引器、事件声明。
    /// </summary>
    public int UnsafeOtherModifiersStripped { get; set; }

    /// <summary>各位置 unsafe 修饰符剥除总数。</summary>
    public int UnsafeModifiersStripped =>
        UnsafeTypeModifiersStripped
        + UnsafeMethodModifiersStripped
        + UnsafeFieldModifiersStripped
        + UnsafeConstructorModifiersStripped
        + UnsafeLocalFunctionModifiersStripped
        + UnsafeOtherModifiersStripped;

    // ---- 后续里程碑预留 ----

    /// <summary>Error 拒绝数（M0 仅可能来自 UF010）。</summary>
    public int Rejections { get; set; }

    /// <summary>residual 闸门命中数。</summary>
    public int ResidualCount { get; set; }

    /// <summary>隐式 stackalloc 收编数（M2）。</summary>
    public int ImplicitStackAllocCollected { get; set; }
}
