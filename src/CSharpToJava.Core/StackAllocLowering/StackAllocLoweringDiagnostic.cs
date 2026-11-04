namespace CSharpToJava.Core.StackAllocLowering;

/// <summary>SL 诊断级别。</summary>
public enum StackAllocLoweringSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// SL 诊断码。
/// <list type="bullet">
/// <item><c>SL001</c>：该 stackalloc 站点无法安全降级，原文保留，交由
/// <c>normalization-audit</c> 继续报 <c>NORM-STACKALLOC</c>。</item>
/// <item><c>SL010</c>：本 pass 内部问题（输出语法校验失败/验证失败）。</item>
/// </list>
/// </summary>
public enum StackAllocLoweringDiagnosticCode
{
    /// <summary>stackalloc 站点无法安全降级，原文保留。</summary>
    SL001,

    /// <summary>pass 内部规则缺口或中间树验证失败。</summary>
    SL010,
}

/// <summary>单条 stackalloc-lowering 诊断，经适配器既有通道落盘。</summary>
public sealed record StackAllocLoweringDiagnostic(
    StackAllocLoweringSeverity Severity,
    StackAllocLoweringDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning SL001: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            StackAllocLoweringSeverity.Error => "error",
            StackAllocLoweringSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
