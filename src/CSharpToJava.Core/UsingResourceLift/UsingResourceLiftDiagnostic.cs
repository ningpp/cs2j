namespace CSharpToJava.Core.UsingResourceLift;

/// <summary>USL diagnostic severity.</summary>
public enum UsingResourceLiftSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// USL diagnostic codes.
/// <list type="bullet">
/// <item><c>USL001</c>: the using resource contains a ref/out read-back but the
/// declaration shape (multiple variables) cannot be lifted; the statement stays
/// verbatim so the Java gate still reports it.</item>
/// <item><c>USL010</c>: the pass itself failed (output parse validation).</item>
/// </list>
/// </summary>
public enum UsingResourceLiftDiagnosticCode
{
    /// <summary>Using resource preserved; the shape is not liftable.</summary>
    USL001,

    /// <summary>Pass-internal failure such as local parse validation.</summary>
    USL010,
}

/// <summary>One using-resource-lift diagnostic, surfaced through the adapter's existing channel.</summary>
public sealed record UsingResourceLiftDiagnostic(
    UsingResourceLiftSeverity Severity,
    UsingResourceLiftDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild form: <c>file(line,col): warning USL001: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            UsingResourceLiftSeverity.Error => "error",
            UsingResourceLiftSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }
}
