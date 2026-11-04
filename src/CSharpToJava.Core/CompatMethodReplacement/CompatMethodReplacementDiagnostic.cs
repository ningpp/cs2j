namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>CMR diagnostic severity.</summary>
public enum CompatMethodReplacementSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// CMR diagnostic codes.
/// <list type="bullet">
/// <item><c>CMR001</c>: a registry entry was not matched — the declaration has the
/// same type/method name but a different namespace, parameter signature or
/// ref-kind set, or the entry never matched any declaration in the file set.</item>
/// <item><c>CMR010</c>: the pass itself failed (for example local parse
/// validation of the rewritten output).</item>
/// </list>
/// </summary>
public enum CompatMethodReplacementDiagnosticCode
{
    /// <summary>Registry entry not found / not matched (near-miss or unused entry).</summary>
    CMR001,

    /// <summary>Pass-internal failure such as local parse validation.</summary>
    CMR010,
}

/// <summary>One compat-method-replacement diagnostic, surfaced through the adapter's existing channel.</summary>
public sealed record CompatMethodReplacementDiagnostic(
    CompatMethodReplacementSeverity Severity,
    CompatMethodReplacementDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild form: <c>file(line,col): warning CMR001: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            CompatMethodReplacementSeverity.Error => "error",
            CompatMethodReplacementSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
