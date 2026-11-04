namespace CSharpToJava.Core.UnsafeRefLowering;

/// <summary>URL diagnostic severity.</summary>
public enum UnsafeRefLoweringSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// URL diagnostic codes.
/// <list type="bullet">
/// <item><c>URL001</c>: a <c>MemoryMarshal.GetReference</c> ref local could not
/// be proven safe to lower; the declaration and every use stay verbatim for
/// <c>normalization-audit</c> (NORM-REF-LOCAL).</item>
/// <item><c>URL010</c>: the pass itself failed (output parse validation).</item>
/// </list>
/// </summary>
public enum UnsafeRefLoweringDiagnosticCode
{
    /// <summary>Ref local preserved; proof failed.</summary>
    URL001,

    /// <summary>Pass-internal failure such as local parse validation.</summary>
    URL010,
}

/// <summary>One unsafe-ref-lowering diagnostic, surfaced through the adapter's existing channel.</summary>
public sealed record UnsafeRefLoweringDiagnostic(
    UnsafeRefLoweringSeverity Severity,
    UnsafeRefLoweringDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild form: <c>file(line,col): warning URL001: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            UnsafeRefLoweringSeverity.Error => "error",
            UnsafeRefLoweringSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
