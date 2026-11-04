namespace CSharpToJava.Core.UnsafeRefLowering;

/// <summary>
/// Options for the independent <c>unsafe-ref-lowering</c> ABI pass. The pass
/// rewrites the Java-unrepresentable
/// <c>ref T x = ref MemoryMarshal.GetReference(span)</c> shape (plus the
/// <c>Unsafe.ReadUnaligned</c>/<c>Unsafe.Add</c> reads that consume it) into
/// reads directly on the span. Everything that cannot be proven equivalent is
/// preserved verbatim and reported (URL001).
/// </summary>
public sealed class UnsafeRefLoweringOptions
{
    /// <summary>Lower <c>Unsafe.ReadUnaligned&lt;U&gt;(ref x)</c> to <c>MemoryMarshal.Read&lt;U&gt;(span)</c>.</summary>
    public bool LowerReadUnaligned { get; init; } = true;

    /// <summary>Lower <c>Unsafe.Add(ref x, k)</c> value reads to <c>span[k]</c>.</summary>
    public bool LowerAddValueReads { get; init; } = true;
}
