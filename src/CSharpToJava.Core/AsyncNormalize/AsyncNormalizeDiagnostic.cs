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
