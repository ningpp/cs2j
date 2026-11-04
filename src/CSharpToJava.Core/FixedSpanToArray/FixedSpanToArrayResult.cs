namespace CSharpToJava.Core.FixedSpanToArray;

/// <summary>
/// Single-file result of <see cref="FixedSpanToArrayPreprocessor.RewriteFile"/>.
/// When <see cref="Changed"/> is false, <see cref="OutputCode"/> is the original
/// source verbatim.
/// </summary>
public sealed record FixedSpanToArrayResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required FixedSpanToArrayStatistics Statistics { get; init; }
    public required IReadOnlyList<FixedSpanToArrayDiagnostic> Diagnostics { get; init; }

    /// <summary>Whether any Error-severity diagnostic was produced.</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == FixedSpanToArraySeverity.Error);
}
