namespace CSharpToJava.Core.StackAllocLowering;

/// <summary>
/// Options for the independent read-only post-pass <c>stackalloc-lowering</c>.
/// The pass runs after <c>unsafe-ffm</c> and only rewrites stackalloc sites that
/// Java can never accept: Span/ReadOnlySpan targets and the provable
/// "pointer local + Span construction" shape. Everything else is preserved and
/// reported (SL001) for the final normalization audit.
/// </summary>
public sealed class StackAllocLoweringOptions
{
    /// <summary>Lower Span/ReadOnlySpan-target stackalloc sites into heap arrays.</summary>
    public bool LowerSpanTargets { get; init; } = true;

    /// <summary>
    /// Lower the <c>T* ptr = stackalloc T[n]; new Span&lt;T&gt;(ptr, n)</c>
    /// shape when the pointer symbol is provably used only as that constructor
    /// argument (or not used at all).
    /// </summary>
    public bool LowerPointerLocals { get; init; } = true;
}
