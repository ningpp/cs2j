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
}
