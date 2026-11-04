namespace CSharpToJava.Core.PointerDerefIndex;

/// <summary>PDI 诊断级别。</summary>
public enum PointerDerefIndexSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// PDI 诊断码。
/// <list type="bullet">
/// <item><c>PDI001</c>：无法证明指针操作数（或形态保守：自增/自减/赋值接收者），
/// 解引用原样保留，继续由 <c>normalization-audit</c> 报 <c>NORM-POINTER-OP</c>。</item>
/// <item><c>PDI010</c>：本 pass 内部问题（输出语法校验失败/验证失败）。</item>
/// </list>
/// </summary>
public enum PointerDerefIndexDiagnosticCode
{
    /// <summary>解引用无法安全改写，原样保留。</summary>
    PDI001,

    /// <summary>pass 内部规则缺口或中间树验证失败。</summary>
    PDI010,
}

/// <summary>单条 pointer-deref-index 诊断，经适配器既有通道落盘。</summary>
public sealed record PointerDerefIndexDiagnostic(
    PointerDerefIndexSeverity Severity,
    PointerDerefIndexDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning PDI001: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            PointerDerefIndexSeverity.Error => "error",
            PointerDerefIndexSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
