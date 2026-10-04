namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 选项（设计 §2 D14、§4.5）。翻转后默认关、长期 opt-in；
/// <see cref="Strict"/> 对接 <c>PassChainConfiguration.Strict</c>。
/// </summary>
/// <param name="Strict">true（默认）时不可归一形态为 Error；false 时 1801-1808 降 Warning，节点原样保留。</param>
/// <param name="Mode">归一形态；<see cref="AsyncNormalizeMode.Heavyweight"/> 为保留值，入口选择即抛。</param>
/// <param name="LiftAsyncLambdas">async lambda 提升总开关（1b 阶段起消费；1a 仅保留选项面）。</param>
/// <param name="StripConfigureAwait">是否把 <c>ConfigureAwait(bool)</c> 归一剥离为 receiver（非严格模式也照剥）。</param>
/// <param name="RejectAsyncVoid">是否对用户书写的 async void 方法/局部函数发 CS2J1803。</param>
public sealed record AsyncNormalizeOptions(
    bool Strict = true,
    AsyncNormalizeMode Mode = AsyncNormalizeMode.Lightweight,
    bool LiftAsyncLambdas = true,
    bool StripConfigureAwait = true,
    bool RejectAsyncVoid = true)
{
    /// <summary>
    /// C38 异步流子域总闸（设计 D10，仅代码面、不暴露 CLI）。pass 自身默认关，
    /// 故此处默认 true 对既有链路零影响；C18 单测直构 Options 时无 C38 语法面即零改写。
    /// </summary>
    public bool EnableAsyncStreams { get; init; } = true;

    /// <summary>
    /// 3a：<c>await using</c> → try/finally + await DisposeAsync() 展开开关。
    /// 关闭时接口资源站点发 CS2J1841。
    /// </summary>
    public bool EnableAwaitUsing { get; init; } = true;

    /// <summary>
    /// 3b：<c>await foreach</c> → 显式 IAsyncEnumerator 驱动（GetAsyncEnumerator +
    /// while (await MoveNextAsync()) + finally DisposeAsync）展开开关（设计 D10）。
    /// 关闭时 await foreach 站点与 IAsyncEnumerable/IAsyncEnumerator 非语句使用点发 CS2J1841。
    /// </summary>
    public bool EnableAwaitForeach { get; init; } = true;

    /// <summary>
    /// 3b：await foreach 展开是否发射「在途主异常优先」释放守卫（成功标志位 +
    /// 在途异常分支吞掉 DisposeAsync 异常，设计 D6 的原假设形态）。
    /// <para><b>net10 真值（tests/SampleAsyncSolution，net10.0/SDK 10.0.300 可复跑，
    /// T4 锁定）。</b>实测 await foreach 等价于朴素 try/finally +
    /// <c>await DisposeAsync()</c>：在途 MoveNextAsync/循环体异常与 DisposeAsync
    /// 异常同时发生时，finally 的 DisposeAsync 异常屏蔽在途主异常（同步/异步故障
    /// 皆然）；正常结束路径 DisposeAsync 异常也照常上抛。await using 同样是朴素
    /// try/finally 语义。即不存在设计文档 D6 假设的「C# 8 在途异常优先」特例，
    /// 故本选项默认 <c>false</c>（发射单层 try/finally，与 .NET 行为一致）。</para>
    /// <para>保留 <c>true</c> 仅作为显式需要「在途异常优先、吞掉释放异常」语义时的
    /// 选择；<b>注意该语义与真实 .NET 行为不同</b>，不要作为保真映射使用。</para>
    /// </summary>
    public bool FaithfulAsyncDisposeGuard { get; init; } = false;

    /// <summary>
    /// 3b：异步迭代器方法（async + IAsyncEnumerable/IEnumerator 返回 + yield）kill-switch
    /// （设计 D7/D10）。v1 恒 true：命中即 CS2J1844 整成员 skip。false 时不诊断但也不展开
    /// （M4 联合状态机之前无其他归宿）。
    /// </summary>
    public bool RejectAsyncIterator { get; init; } = true;
}
