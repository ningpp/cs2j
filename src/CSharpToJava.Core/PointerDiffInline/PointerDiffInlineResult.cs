namespace CSharpToJava.Core.PointerDiffInline;

/// <summary>
/// Single-file result of <see cref="PointerDiffInlinePreprocessor.RewriteFile"/>.
/// When <see cref="Changed"/> is false, <see cref="OutputCode"/> is the original
/// source verbatim.
/// </summary>
public sealed record PointerDiffInlineResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required PointerDiffInlineStatistics Statistics { get; init; }
    public required IReadOnlyList<PointerDiffInlineDiagnostic> Diagnostics { get; init; }

    /// <summary>Whether any Error-severity diagnostic was produced.</summary>
    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == PointerDiffInlineSeverity.Error);
}
