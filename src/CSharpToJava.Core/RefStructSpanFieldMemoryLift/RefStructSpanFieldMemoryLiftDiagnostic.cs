namespace CSharpToJava.Core.RefStructSpanFieldMemoryLift;

/// <summary>RSM 诊断级别。</summary>
public enum RefStructSpanFieldMemoryLiftSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// RSM 诊断码。
/// <list type="bullet">
/// <item><c>RSM001</c>：该 ref struct 含无法安全内存化的成员（可变 Span/ref/
/// 指针字段、非只读 span 字段、字段初始化器、取址/ref/out 引用、嵌套 ref
/// struct 等），整型保留，交由 <c>normalization-audit</c> 继续报
/// <c>NORM-REF-STRUCT</c>。</item>
/// <item><c>RSM010</c>：本 pass 内部问题（输出语法校验失败/验证失败）。</item>
/// </list>
/// </summary>
public enum RefStructSpanFieldMemoryLiftDiagnosticCode
{
    /// <summary>ref struct 无法安全内存化，整型保留。</summary>
    RSM001,

    /// <summary>pass 内部规则缺口或中间树验证失败。</summary>
    RSM010,
}

/// <summary>单条 ref-struct-span-field-memory-lift 诊断，经适配器既有通道落盘。</summary>
public sealed record RefStructSpanFieldMemoryLiftDiagnostic(
    RefStructSpanFieldMemoryLiftSeverity Severity,
    RefStructSpanFieldMemoryLiftDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning RSM001: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            RefStructSpanFieldMemoryLiftSeverity.Error => "error",
            RefStructSpanFieldMemoryLiftSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
