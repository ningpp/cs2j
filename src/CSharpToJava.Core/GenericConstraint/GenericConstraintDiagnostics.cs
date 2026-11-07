using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.GenericConstraint;

public enum GenericConstraintSeverity { Info, Warning, Error }

/// <summary>The CN rule family a diagnostic belongs to (R2 table, upgrade plan §6.4-R2).</summary>
public enum GenericConstraintRule
{
    CN01,
    CN02,
    CN03,
    CN04,
    CN05,
    CN06,
    CN07,
    CN08,
    CN09,
    CN10,
    CN11,
    CN12,

    /// <summary>Infrastructure/validation diagnostics (rewrite verification, restore/build/test gates).</summary>
    Infrastructure,
}

public sealed record GenericConstraintDiagnostic(
    GenericConstraintSeverity Severity,
    GenericConstraintRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            GenericConstraintSeverity.Error => "error",
            GenericConstraintSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}:{Line?.ToString() ?? "?"}: {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>
/// Diagnostic codes for the generic-constraint pass. The values live in the
/// shared <see cref="PreprocessDiagnosticCodes"/> table so the CN family and
/// the downstream converter cannot drift apart.
/// </summary>
public static class GenericConstraintCodes
{
    /// <summary>CN-04/09/10/12: a constraint or rewrite site cannot be expressed safely; the site is preserved.</summary>
    public const string ConstraintNotExpressible = PreprocessDiagnosticCodes.GenericConstraintNotExpressible;

    /// <summary>CN-08/CN-11: an explicitly unsupported generic shape; the site is preserved verbatim.</summary>
    public const string DeclaredUnsupported = PreprocessDiagnosticCodes.GenericConstraintDeclaredUnsupported;
}
