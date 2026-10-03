namespace CSharpToJava.Core.VarianceAudit;

public enum VarianceSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// VA0xx diagnostic codes for the variance-audit pass (design §6). Codes are
/// append-only: never renumber an existing entry.
/// </summary>
public static class VarianceCodes
{
    // 001-009 declaration inventory (Info)
    public const string DeclarationSite = "VA001";

    // 010-019 use-site variance and mapped mutability
    public const string CovariantConversionSite = "VA010";
    public const string ContravariantConversionSite = "VA011";
    public const string MappedMutabilityExpansion = "VA012";
    public const string ProducerUseSite = "VA013";
    public const string ConsumerUseSite = "VA014";
    public const string BothConversionSite = "VA015";
    public const string NestedCompositionSite = "VA016";

    // 020-029 constraints and their dependent sites
    public const string StructConstraint = "VA020";
    public const string UnmanagedConstraint = "VA021";
    public const string NewConstraint = "VA022";
    public const string ResidualNewTSite = "VA023";
    public const string NotNullConstraint = "VA024";
    public const string DefaultConstraint = "VA025";
    public const string ClassConstraint = "VA026";
    public const string DefaultOnValueTypeConstraint = "VA027";
    public const string ValueTypeDependentSite = "VA028";

    // 030 strict-mode aggregate gate
    public const string StrictGate = "VA030";

    // 050-052 infrastructure (unreachable while verify stays off)
    public const string RestoreFailed = "VA050";
    public const string BuildFailed = "VA051";
    public const string TestsFailed = "VA052";

    // 070 report written
    public const string ReportWritten = "VA070";
}

/// <summary>One variance-audit diagnostic in MSBuild-parseable form.</summary>
public sealed record VarianceAuditDiagnostic(
    VarianceSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild format: <c>file(line,col): warning VA0xx: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            VarianceSeverity.Error => "error",
            VarianceSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{loc}{level} {Code}: {Message}";
    }
}