namespace CSharpToJava.Core.PointerDiffInline;

/// <summary>
/// Options for the independent <c>pointer-diff-inline</c> expression pass. The
/// pass runs after <c>ref-out-in</c> and before <c>unsafe-ffm</c>, inlining the
/// file-local "pointer difference to integer" helper methods (for example
/// <c>JsonWriterHelper.PtrDiff</c>) at their call sites so the helper's pointer
/// parameters no longer escape into a method call (UF020).
/// </summary>
public sealed class PointerDiffInlineOptions
{
    /// <summary>
    /// Inline provable same-type pointer-difference helpers into
    /// <c>(R)(a - b)</c> and remove the helper declaration when it is no longer
    /// referenced.
    /// </summary>
    public bool InlinePointerDifferences { get; init; } = true;
}
