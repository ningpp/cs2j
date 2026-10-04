namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 统计计数器（设计 §4.6）。
/// 通用 5 字段对齐 iterator-lowering/concurrency 族；专有桶只建 1a 已有消费面的，
/// lambda 提升桶（AsyncLambdasLifted/AsyncLocalFunctionsLifted/WrappersGenerated/
/// CapturedAsyncLambdasDeferred）随 1b/3 阶段再追加。
/// </summary>
public sealed class AsyncNormalizeStatistics
{
    // ── Common 5 fields ──
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;

    // ── Pass-specific fields (M1a) ──

    /// <summary>带 async 修饰符的方法/局部函数数（触碰计数，轻量版不清零）。</summary>
    public int AsyncMethodsSeen;

    /// <summary>await 站点总数（轻量版保留 await，计数不动）。</summary>
    public int AwaitSites;

    /// <summary>已剥离的 ConfigureAwait(bool) 调用数。</summary>
    public int ConfigureAwaitStripped;

    /// <summary>被拒绝的用户 async void 方法/局部函数数（CS2J1803）。</summary>
    public int AsyncVoidRejected;

    /// <summary>不支持的 awaitable 站点数（await dynamic / 自定义 awaiter，CS2J1801）。</summary>
    public int UnsupportedAwaiters;

    /// <summary>不可归一、原样保留的站点数（1801/1803/1805/1808；后续按原因分桶）。</summary>
    public int SkippedUnsupported;

    // ── Pass-specific fields (M1b: async lambda/local-function lifting, D3/D4) ──

    /// <summary>零捕获提为 private static async 方法的 async lambda 数（D3(a)）。</summary>
    public int AsyncLambdasLiftedZeroCapture;

    /// <summary>仅捕获 this、提为 private async 实例方法的 async lambda 数（D3(b)）。</summary>
    public int AsyncLambdasLiftedThisOnly;

    /// <summary>void 返回委托（async void lambda）经 Task 方法 + 同步 wrapper 提升的站点数（D3(c)）。</summary>
    public int AsyncLambdasLiftedVoidWrapper;

    /// <summary>残余 async 局部函数提升为类型级方法的数量（D4）。</summary>
    public int AsyncLocalFunctionsLifted;

    /// <summary>合成的 _w 同步丢弃 wrapper 数（与 VoidWrapper 站点 1:1）。</summary>
    public int WrappersGenerated;

    /// <summary>捕获局部/形参/foreach 变量、v1 defer 原样保留的 async lambda 数（D3(d)/CS2J1802）。</summary>
    public int CapturedAsyncLambdasDeferred;

    // ── Pass-specific fields (C38 M2/3a: await using → try/finally + DisposeAsync, D4/D5/D8) ──

    /// <summary>已展开的 await using 语句形态站点数（<c>await using (…) …</c>，多变量仍计 1 站点）。</summary>
    public int AwaitUsingStatementsExpanded;

    /// <summary>已展开的 await using 声明形态站点数（<c>await using var r = …;</c>）。</summary>
    public int AwaitUsingDeclarationsExpanded;

    /// <summary>finally 内发射的 null/HasValue 守卫数（引用类型 + Nullable 值类型资源）。</summary>
    public int AsyncDisposeGuardsEmitted;

    /// <summary>放行零改写的 IAsyncDisposable 类型使用点数（参数/字段/属性/返回/局部，非语句位）。</summary>
    public int AsyncTypeSitesMapped;

    /// <summary>选项关闭被拒（CS2J1841）的 await using 接口资源站点数。</summary>
    public int AsyncTypeSitesRejected;

    /// <summary>鸭子型/ref struct 异步释放被 defer 的站点数（CS2J1842）。</summary>
    public int DuckTypedAsyncDeferred;

    // ── Pass-specific fields (C38 M3/3b: await foreach → explicit async enumerator, D2/D3/D6/D7) ──

    /// <summary>已展开的 await foreach 站点数（AS-1/AS-2/AS-3）。</summary>
    public int AwaitForeachExpanded;

    /// <summary>await foreach 展开发射的枚举器临时量数（与展开站点 1:1）。</summary>
    public int AsyncEnumeratorsEmitted;

    /// <summary>消解进 GetAsyncEnumerator 实参的 WithCancellation(token) 链数（AS-2）。</summary>
    public int WithCancellationResolved;

    /// <summary>剥离为 receiver 的枚举器/异步枚举 ConfigureAwait(bool) 链数（CS2J1830 Info）。</summary>
    public int EnumerableConfigureAwaitStripped;

    /// <summary>
    /// 被识别并整成员 skip 的异步迭代器数（CS2J1844，AS-6）：M4 后仅剩局部函数、
    /// v1 不支持形态与 kill-switch 命中；方法形态改由 <see cref="AsyncIteratorsLowered"/> 承接。
    /// </summary>
    public int AsyncIteratorsRejected;

    // ── Pass-specific fields (C38 M4: 联合状态机 async-iterator lowering，D7/§4.4) ──

    /// <summary>降级为「非 async 工厂 + 生成枚举器状态机」的 async-iterator 方法数。</summary>
    public int AsyncIteratorsLowered;

    /// <summary>lowering 发射的枚举器类 + 可枚举包装类总数。</summary>
    public int AsyncIteratorClassesEmitted;

    /// <summary>lowering 发射的 yield 区域数（try-finally/同步 using/同步 foreach）。</summary>
    public int AsyncIteratorRegionsEmitted;

    /// <summary>
    /// C38 residual 分项（await foreach/await using；不含 C18 面）。仅供适配器
    /// AfterFilesAsync 聚合兜底 CS2J1849 使用；<see cref="ResidualCount"/> 仍为 C18+C38 合计。
    /// </summary>
    public int AsyncStreamResidualCount;
}
