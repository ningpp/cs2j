namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 统计计数器（设计 §4「按规则命中数、拒绝数、residual 数」）。
/// M0 记 unsafe 段（unsafe 块与各位置 unsafe 修饰符剥除数）；M1 追加
/// R2 fixed 字段改写字段数、R3 fixed 语句数（按初始式五类分类）以及
/// UF020/UF030/UF031 拒绝数；M2 追加 R4 显式 stackalloc 收编数、隐式
/// stackalloc 收编数与 UF040 拒绝数；M3a 追加 R5 签名改写数（形参/
/// 返回/局部，含指针+长度模式）、R6 指针表达式索引化数与 UF021/UF022
/// 拒绝数；M3b 追加算术传播数、自增数、比较数、差值数；M4 追加
/// UF050（用户 ref struct 声明）、UF051（InlineArray）、UF055
/// （非白名单 ref struct using）、UF060（extern-like）、UF070
/// （Marshal/NativeLibrary/SafeHandle/HandleRef）计数与 Span using
/// 空操作归一数。
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

    // ---- R4：stackalloc 统一堆化 ----

    /// <summary>显式 stackalloc（StackAllocArrayCreationExpressionSyntax）收编数。</summary>
    public int ExplicitStackAllocCollected { get; set; }

    /// <summary>隐式 stackalloc（ImplicitStackAllocArrayCreationExpressionSyntax）收编数。</summary>
    public int ImplicitStackAllocCollected { get; set; }

    // ---- 拒绝 / residual ----

    /// <summary>Error 拒绝总数（M0：UF010；M1 起含 UF020/UF030/UF031；M2 起含 UF040 等）。</summary>
    public int Rejections { get; set; }

    /// <summary>residual 闸门命中数。</summary>
    public int ResidualCount { get; set; }

    /// <summary>UF020 拒绝数（指针不可静态证明/逃逸）。</summary>
    public int Uf020Rejections { get; set; }

    /// <summary>UF030 拒绝数（fixed 字符串缓冲写入）。</summary>
    public int Uf030Rejections { get; set; }

    /// <summary>UF031 拒绝数（fixed 字段所在类型布局锁且与 extern 关联）。</summary>
    public int Uf031Rejections { get; set; }

    /// <summary>UF040 拒绝数（stackalloc 目标为裸指针或元素类型不可堆化）。</summary>
    public int Uf040Rejections { get; set; }

    // ---- R5：指针签名白名单 → 数组 ----

    /// <summary>指针形参改写数（按 ParameterSyntax 计，含「指针+长度」模式的指针）。</summary>
    public int PointerParametersRewritten { get; set; }

    /// <summary>指针返回类型改写数（按成员计）。</summary>
    public int PointerReturnsRewritten { get; set; }

    /// <summary>指针局部变量改写数（源自 R5 改写参数/局部或 R2 改写字段，按变量计）。</summary>
    public int PointerLocalsRewritten { get; set; }

    /// <summary>「指针+长度」相邻参数模式命中数（指针仍按形参改写计数，此为模式旁证计数）。</summary>
    public int PointerLengthPatterns { get; set; }

    /// <summary>R5 签名改写总数：形参 + 返回。</summary>
    public int PointerSignaturesRewritten =>
        PointerParametersRewritten + PointerReturnsRewritten;

    // ---- R6：指针表达式索引化 ----

    /// <summary>
    /// 指针表达式索引化数：<c>*p</c>→<c>arr[idx]</c>、<c>p[i]</c>→<c>arr[idx+i]</c>
    /// （按重写表达式节点计；文本不变的同构节点不计）。
    /// </summary>
    public int PointerExprsIndexized { get; set; }

    /// <summary>UF021 拒绝数（void*/delegate*/泛型指针/非基元 struct 指针/指针↔整数转换）。</summary>
    public int Uf021Rejections { get; set; }

    /// <summary>UF022 拒绝数（M3b 前不可回填的指针算术/差值/比较/++--）。</summary>
    public int Uf022Rejections { get; set; }

    // ---- R6 / M3b：指针算术偏移传播 ----

    /// <summary>
    /// 算术传播数：<c>p+n</c>/<c>n+p</c>/<c>p-n</c>（含 <c>p+=n</c>/<c>p-=n</c>）
    /// 在模型内传播为同源 int 偏移表达式（按最终物化点的节点计）。
    /// </summary>
    public int PointerArithmeticPropagated { get; set; }

    /// <summary>自增数：<c>p++</c>/<c>++p</c>/<c>p--</c>/<c>--p</c>（索引局部自增，按节点计）。</summary>
    public int PointerIncrements { get; set; }

    /// <summary>比较数：同源指针 <c>&lt; &gt; &lt;= &gt;= == !=</c> → int 索引比较（按节点计）。</summary>
    public int PointerComparisons { get; set; }

    /// <summary>差值数：同源 <c>p-q</c> → int 索引差（按节点计；异源不计，发 UF020）。</summary>
    public int PointerDifferences { get; set; }

    // ---- M4：ref struct / InlineArray / extern ----

    /// <summary>UF050 拒绝数（用户自定义 ref struct/readonly ref struct 声明）。</summary>
    public int Uf050Rejections { get; set; }

    /// <summary>UF051 拒绝数（[InlineArray] 标注 struct）。</summary>
    public int Uf051Rejections { get; set; }

    /// <summary>UF055 拒绝数（非白名单 ref struct 的 using 站点）。</summary>
    public int Uf055Rejections { get; set; }

    /// <summary>UF060 拒绝数（extern/DllImport/LibraryImport 非白名单）。</summary>
    public int Uf060Rejections { get; set; }

    /// <summary>UF061 拒绝数（白名单命中但封送布局不可证明：bool/ref/struct 等）。</summary>
    public int Uf061Rejections { get; set; }

    /// <summary>R8 opt-in 白名单归一数：extern 方法 → InteropFacade 受控声明。</summary>
    public int FfmFacadesEmitted { get; set; }

    /// <summary>UF070 拒绝数（Marshal/NativeLibrary/SafeHandle/HandleRef 站点）。</summary>
    public int Uf070Rejections { get; set; }

    /// <summary>
    /// Span using 空操作归一数：<c>using Span<T>/ReadOnlySpan<T></c>
    /// （using declaration/block）剥除 using 的站点数（无资源释放语义）。
    /// </summary>
    public int SpanUsingNoOps { get; set; }
}
