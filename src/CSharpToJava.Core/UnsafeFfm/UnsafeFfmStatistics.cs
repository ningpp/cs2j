namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 统计计数器（设计 §4「按规则命中数、拒绝数、residual 数」）。
/// M0 记 unsafe 段（unsafe 块与各位置 unsafe 修饰符剥除数）；M1 追加
/// R2 fixed 字段改写字段数、R3 fixed 语句数（按初始式五类分类）以及
/// UF020/UF030/UF031 拒绝数；隐式 stackalloc 等计数器为后续里程碑预留。
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

    // ---- R2：fixed 字段缓冲区 → 数组字段 ----

    /// <summary>fixed 字段缓冲区改写数（按 VariableDeclarator 计）。</summary>
    public int FixedBufferFieldsRewritten { get; set; }

    // ---- R3：fixed 语句 ----

    /// <summary>fixed 语句数（按 FixedStatementSyntax 节点计）。</summary>
    public int FixedStatementsCount { get; set; }

    /// <summary>初始式=数组（含 R2 改写后的 fixed 字段）的固定指针数。</summary>
    public int FixedVariablesArray { get; set; }

    /// <summary>初始式=&arr[k]（带偏移取址）的固定指针数。</summary>
    public int FixedVariablesOffset { get; set; }

    /// <summary>初始式=指针别名（tracked pointer）的固定指针数。</summary>
    public int FixedVariablesAlias { get; set; }

    /// <summary>初始式=null 的固定指针数。</summary>
    public int FixedVariablesNull { get; set; }

    /// <summary>初始式=string 的固定指针数。</summary>
    public int FixedVariablesString { get; set; }

    /// <summary>初始式=&scalar（标量单槽）的固定指针数。</summary>
    public int FixedVariablesScalar { get; set; }

    /// <summary>固定指针变量总数（一个 fixed 语句可声明多个）。</summary>
    public int FixedVariablesTotal =>
        FixedVariablesArray + FixedVariablesOffset + FixedVariablesAlias
        + FixedVariablesNull + FixedVariablesString + FixedVariablesScalar;

    // ---- 拒绝 / residual ----

    /// <summary>Error 拒绝总数（M0：UF010；M1 起含 UF020/UF030/UF031 等）。</summary>
    public int Rejections { get; set; }

    /// <summary>residual 闸门命中数。</summary>
    public int ResidualCount { get; set; }

    /// <summary>UF020 拒绝数（指针不可静态证明/逃逸）。</summary>
    public int Uf020Rejections { get; set; }

    /// <summary>UF030 拒绝数（fixed 字符串缓冲写入）。</summary>
    public int Uf030Rejections { get; set; }

    /// <summary>UF031 拒绝数（fixed 字段所在类型布局锁且与 extern 关联）。</summary>
    public int Uf031Rejections { get; set; }

    /// <summary>隐式 stackalloc 收编数（M2）。</summary>
    public int ImplicitStackAllocCollected { get; set; }
}
