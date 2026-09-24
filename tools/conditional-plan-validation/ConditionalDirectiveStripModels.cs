namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public enum ConditionalDirectiveSeverity { Warning, Error }

public sealed record ConditionalDirectiveDiagnostic(
    ConditionalDirectiveSeverity Severity,
    string Message,
    int? Line = null);

public sealed class ConditionalDirectiveStatistics
{
    public int DirectivesRemoved;
    public int DisabledBranchesRemoved;
}

public sealed record ConditionalDirectiveStripResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ConditionalDirectiveDiagnostic> Diagnostics,
    ConditionalDirectiveStatistics Statistics);

public sealed record ConditionalDirectiveStripOptions
{
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
    public bool RemoveRegions { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
}
