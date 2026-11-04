namespace CSharpToJava.Core.UsingResourceLift;

/// <summary>
/// Single-file result of <see cref="UsingResourceLiftPreprocessor.RewriteFile"/>.
/// When <see cref="Changed"/> is false, <see cref="OutputCode"/> is the original
/// source verbatim.
/// </summary>
public sealed record UsingResourceLiftResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required UsingResourceLiftStatistics Statistics { get; init; }
    public required IReadOnlyList<UsingResourceLiftDiagnostic> Diagnostics { get; init; }

    /// <summary>Whether any Error-severity diagnostic was produced.</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UsingResourceLiftSeverity.Error);
}
