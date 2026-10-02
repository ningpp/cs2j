namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>Severity of an expression-desugar family diagnostic.</summary>
public enum ExpressionDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic emitted by the expression-desugar family (C23/C24/C35/C36).
/// <see cref="Code"/> carries the literal MSBuild-style code (e.g. "CS2J0570");
/// each rule owns its own code range and templates. The shape mirrors the C47
/// equality-concat record.
/// </summary>
public sealed record ExpressionDesugarDiagnostic(
    ExpressionDesugarSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild format: <c>file(line,col): warning CS2J05xx: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            ExpressionDesugarSeverity.Error => "error",
            ExpressionDesugarSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{loc}{level} {Code}: {Message}";
    }
}
