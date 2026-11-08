namespace CSharpToJava.Core.FixedSpanToArray;

/// <summary>
/// Options for the independent <c>fixed-span-to-array</c> ABI pass. The pass
/// rewrites <c>fixed (T* p = span)</c> and
/// <c>fixed (T* p = &amp;MemoryMarshal.GetReference(span))</c> into a
/// managed-array fixed so the later <c>unsafe-ffm</c> R3 array path can lower it.
/// </summary>
public sealed class FixedSpanToArrayOptions
{
    /// <summary>Lower <c>fixed (T* p = readOnlySpan)</c> into <c>span.ToArray()</c>.</summary>
    public bool LowerReadOnlySpans { get; init; } = true;

    /// <summary>
    /// Lower writable <c>Span&lt;T&gt;</c> targets through a temporary array plus a
    /// <c>try/finally</c> write-back copy. Off by default: the write-back copies the
    /// <em>whole</em> span on every execution, so the cost is O(caller buffer) rather
    /// than O(bytes written) and the caller may pass a large buffer. Opt in only when
    /// the span is known to be small.
    /// </summary>
    public bool LowerWritableSpans { get; init; }
}
