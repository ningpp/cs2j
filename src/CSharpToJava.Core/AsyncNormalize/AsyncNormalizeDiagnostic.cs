namespace CSharpToJava.Core.AsyncNormalize;

public enum AsyncNormalizeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// C18 诊断码（设计 §4.6）。码位只追加不改号。
/// </summary>
public enum AsyncNormalizeDiagnosticCode
{
    /// <summary>不支持的 awaitable：await dynamic / 自定义 task-like（自定义 GetAwaiter）。</summary>
    CS2J1801,

    /// <summary>async lambda 捕获局部/形参/foreach 变量，closure-lift 落地前无法等价提升（D3(d)）。</summary>
    CS2J1802,

    /// <summary>用户 async void 方法/局部函数（D6）。</summary>
    CS2J1803,

    /// <summary>残余未被 E02 提升且本 pass 无法提升的 async 局部函数（D4）。</summary>
    CS2J1804,

    /// <summary>非法 await 站点防御镜像（lock 内 await＝CS1996，基线优先）。</summary>
    CS2J1805,

    /// <summary>残余 async 匿名方法 / 无法绑定委托目标的 async lambda（D3/§4.1）。</summary>
    CS2J1807,

    /// <summary>ConfigureAwait(ConfigureAwaitOptions) / ConfiguredTaskAwaitable 强类型形态（D5）。</summary>
    CS2J1808,

    /// <summary>residual 闸门：输出仍有 ConfigureAwait 调用 / 未豁免 async void。</summary>
    CS2J1810,

    /// <summary>restore/build/test 失败（适配器钩子记录，1a 不发射）。</summary>
    CS2J1820,

    /// <summary>归一明细 Info（剥离的 ConfigureAwait 站点）。</summary>
    CS2J1830,

    // ── C38 异步流子域预留子段 1840-1849 ──

    /// <summary>C38：await foreach 源非 IAsyncEnumerable（鸭子型 v1 不支持）或残余解构形态。</summary>
    CS2J1840,

    /// <summary>C38：await using 资源命中 IAsyncDisposable 但 EnableAwaitUsing 关闭（桥/选项面）。</summary>
    CS2J1841,

    /// <summary>C38：pattern-based（鸭子型）DisposeAsync / ref struct 异步释放，v1 不支持。</summary>
    CS2J1842,

    /// <summary>C38：ConfiguredCancelableAsyncEnumerable&lt;T&gt; 强类型点；枚举器上非 bool ConfigureAwait。</summary>
    CS2J1843,

    /// <summary>C38：异步迭代器方法（async + IAsyncEnumerable/IEnumerator 返回 + yield），v1 不降级。</summary>
    CS2J1844,

    /// <summary>C38：防御镜像——await using 落在无法安全展开/绑定的宿主或资源形态。</summary>
    CS2J1845,

    /// <summary>C38：WithCancellation 实参非 CancellationToken、重复 WithCancellation 或链无法静态消解。</summary>
    CS2J1846,

    /// <summary>C38：residual 闸门——输出仍含未豁免的裸 await foreach/await using 节点。</summary>
    CS2J1849,
}

/// <summary>
/// C18 诊断记录。MSBuild 格式（设计 §6）：
/// <c>文件(行): warning/error CS2J18xx: 描述</c>。
/// 枚举成员名即码值（<see cref="AsyncNormalizeDiagnosticCode.ToString"/> 输出 "CS2J1801" 形态）。
/// </summary>
public sealed record AsyncNormalizeDiagnostic(
    AsyncNormalizeSeverity Severity,
    AsyncNormalizeDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0)
{
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            AsyncNormalizeSeverity.Error => "error",
            AsyncNormalizeSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line}): " : string.Empty;
        return $"{loc}{level} {Code}: {Message}";
    }
}
