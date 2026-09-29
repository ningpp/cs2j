namespace CSharpToJava.Core.PatternDesugar;

public enum PatternDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic codes: PD001 verbose file status (adapter-printed), PD010
/// unreducible pattern, PD011 goto case/default in a lowered switch,
/// PD020 residual target after rewrite, PD030 restore/build/test failure.
/// </summary>
public static class PatternDesugarCodes
{
    public const string Info = "PD001";
    public const string UnsupportedPattern = "PD010";
    public const string GotoCaseInLoweredSwitch = "PD011";
    public const string ResidualTargets = "PD020";
    public const string RestoreOrBuildFailed = "PD030";
}

public sealed record PatternDesugarDiagnostic(
    string Code,
    PatternDesugarSeverity Severity,
    string Message,
    string? FilePath = null);
