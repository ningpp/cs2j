namespace CSharpToJava.Core.UnsafeRefLowering;

/// <summary>
/// Single-file result of <see cref="UnsafeRefLoweringPreprocessor.RewriteFile"/>.
/// When <see cref="Changed"/> is false, <see cref="OutputCode"/> is the original
/// source verbatim.
/// </summary>
public sealed record UnsafeRefLoweringResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required UnsafeRefLoweringStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsafeRefLoweringDiagnostic> Diagnostics { get; init; }

    /// <summary>Whether any Error-severity diagnostic was produced.</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeRefLoweringSeverity.Error);
}
