namespace CSharpToJava.Core.RefStructLowering;

/// <summary>RSL 诊断级别。</summary>
public enum RefStructLoweringSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// RSL 诊断码。
/// <list type="bullet">
/// <item><c>RSL001</c>：该 ref struct 含 ref-only 成员，修饰符保留，交由
/// <c>normalization-audit</c> 继续报 <c>NORM-REF-STRUCT</c>。</item>
/// <item><c>RSL010</c>：本 pass 内部问题（输出语法校验失败/验证失败）。</item>
/// </list>
/// </summary>
public enum RefStructLoweringDiagnosticCode
{
    /// <summary>ref struct 含 ref-only 成员，修饰符保留。</summary>
    RSL001,

    /// <summary>pass 内部规则缺口或中间树验证失败。</summary>
    RSL010,
}

/// <summary>单条 ref-struct-lowering 诊断，经适配器既有通道落盘。</summary>
public sealed record RefStructLoweringDiagnostic(
    RefStructLoweringSeverity Severity,
    RefStructLoweringDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning RSL001: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            RefStructLoweringSeverity.Error => "error",
            RefStructLoweringSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
