namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>UF 诊断级别。</summary>
public enum UnsafeFfmSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>UF0xx 诊断码（设计 §6）。码位只追加，不改号。</summary>
public enum UnsafeFfmDiagnosticCode
{
    /// <summary>residual 闸门：输出树仍残留 unsafe 家族节点（内部错误，指向规则缺口）。</summary>
    UF010,

    /// <summary>指针指向不可静态证明（逃逸/字段/返回/struct 指针 p->m）。</summary>
    UF020,

    /// <summary>不支持的指针种类：void*、delegate*、泛型指针、非 int 整数转换等。</summary>
    UF021,

    /// <summary>指针算术产生不可回填的逃逸偏移。</summary>
    UF022,

    /// <summary>对 fixed 字符串缓冲写入（写穿不可回写）。</summary>
    UF030,

    /// <summary>fixed 字段缓冲区所在类型带布局锁且进入 P/Invoke 签名。</summary>
    UF031,

    /// <summary>stackalloc 目标为裸指针或元素类型不可堆化。</summary>
    UF040,

    /// <summary>用户自定义 ref struct 声明（白名单外）。</summary>
    UF050,

    /// <summary>InlineArray 构造（盘点期拒绝）。</summary>
    UF051,

    /// <summary>非白名单 ref struct 的 using 站点（2026-09-29 裁决新增 R9）。</summary>
    UF055,

    /// <summary>extern/DllImport/LibraryImport 不在 FFM 白名单。</summary>
    UF060,

    /// <summary>白名单条目封送布局不可证明。</summary>
    UF061,

    /// <summary>Marshal/NativeLibrary/SafeHandle/HandleRef 使用。</summary>
    UF070,

    /// <summary>单文件模式下跨文件 plan 跳过（Warning）。</summary>
    UF090,
}

/// <summary>
/// C06 诊断记录。经适配器既有诊断通道落盘，<see cref="ToMsBuildString"/>
/// 产出 MSBuild 兼容文本。
/// </summary>
public sealed record UnsafeFfmDiagnostic(
    UnsafeFfmSeverity Severity,
    UnsafeFfmDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): error UF0xx: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            UnsafeFfmSeverity.Error => "error",
            UnsafeFfmSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
