namespace CSharpToJava.Core.PointerDiffInline;

/// <summary>PDI diagnostic severity.</summary>
public enum PointerDiffInlineSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// PDI diagnostic codes for <c>pointer-diff-inline</c>.
/// <list type="bullet">
/// <item><c>PDI001</c>: a pointer-arithmetic helper was preserved (it is not
/// private static, the pointer element types differ, the parameter list is not
/// a plain two-pointer shape, or the body is not a provable same-type pointer
/// difference).</item>
/// <item><c>PDI010</c>: the pass itself failed (local parse validation or the
/// intermediate build/test gate).</item>
/// </list>
/// </summary>
public enum PointerDiffInlineDiagnosticCode
{
    /// <summary>Pointer-arithmetic helper preserved for a shape/semantic reason.</summary>
    PDI001,

    /// <summary>Pass-internal failure such as local parse validation.</summary>
    PDI010,
}

/// <summary>One pointer-diff-inline diagnostic, surfaced through the adapter's existing channel.</summary>
public sealed record PointerDiffInlineDiagnostic(
    PointerDiffInlineSeverity Severity,
    PointerDiffInlineDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild form: <c>file(line,col): warning PDI001: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            PointerDiffInlineSeverity.Error => "error",
            PointerDiffInlineSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
