namespace CSharpToJava.Core.VarianceUseSite;

/// <summary>VR 诊断级别。</summary>
public enum VarianceUseSiteSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// VR 诊断码。码表按方案 §6.4-R4 固定：
/// <c>CS2J1404</c> 表示站点无法分类/无法安全改写（<c>DeclaredUnsupported</c>），
/// 以及整文件回滚这类本 pass 内部失败。
/// </summary>
public enum VarianceUseSiteDiagnosticCode
{
    /// <summary>站点无法分类/改写；报告仍记录精确 VR 规则与原因。</summary>
    CS2J1404,
}

/// <summary>单条 variance-use-site-normalize 诊断，经适配器既有通道落盘。</summary>
public sealed record VarianceUseSiteDiagnostic(
    VarianceUseSiteSeverity Severity,
    VarianceUseSiteDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning CS2J1404: message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            VarianceUseSiteSeverity.Error => "error",
            VarianceUseSiteSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
