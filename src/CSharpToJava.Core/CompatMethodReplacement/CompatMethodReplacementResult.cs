namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>
/// Single-file result of <see cref="CompatMethodReplacementPreprocessor.RewriteFile"/>.
/// When <see cref="Changed"/> is false, <see cref="OutputCode"/> is the original
/// source verbatim.
/// </summary>
public sealed record CompatMethodReplacementResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required CompatMethodReplacementStatistics Statistics { get; init; }
    public required IReadOnlyList<CompatMethodReplacementDiagnostic> Diagnostics { get; init; }

    /// <summary>Whether any Error-severity diagnostic was produced.</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == CompatMethodReplacementSeverity.Error);
}
