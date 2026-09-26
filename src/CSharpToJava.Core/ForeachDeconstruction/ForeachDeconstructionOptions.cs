namespace CSharpToJava.Core.ForeachDeconstruction;

public sealed class ForeachDeconstructionOptions
{
    /// <summary>Whether to emit Info-level diagnostics for skipped deconstruction foreach.</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>Strict mode: treat skipped deconstruction foreach as errors instead of info.</summary>
    public bool Strict { get; init; }
}
