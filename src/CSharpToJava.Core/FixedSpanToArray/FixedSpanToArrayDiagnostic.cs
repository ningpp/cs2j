namespace CSharpToJava.Core.FixedSpanToArray;

/// <summary>FSA diagnostic severity.</summary>
public enum FixedSpanToArraySeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// FSA diagnostic codes.
/// <list type="bullet">
/// <item><c>FSA001</c>: a span-backed fixed statement could not be lowered
/// safely and was preserved verbatim.</item>
/// <item><c>FSA010</c>: the pass itself failed (for example local parse
/// validation of the rewritten output).</item>
/// </list>
/// </summary>
public enum FixedSpanToArrayDiagnosticCode
{
    /// <summary>Span fixed statement preserved; the shape is not safely lowerable.</summary>
    FSA001,

    /// <summary>Pass-internal failure such as local parse validation.</summary>
    FSA010,
}

/// <summary>One fixed-span-to-array diagnostic, surfaced through the adapter's existing channel.</summary>
public sealed record FixedSpanToArrayDiagnostic(
    FixedSpanToArraySeverity Severity,
    FixedSpanToArrayDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild form: <c>file(line,col): warning FSA001: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            FixedSpanToArraySeverity.Error => "error",
            FixedSpanToArraySeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
