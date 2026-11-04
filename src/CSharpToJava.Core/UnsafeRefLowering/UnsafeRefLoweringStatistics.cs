namespace CSharpToJava.Core.UnsafeRefLowering;

/// <summary>Aggregated statistics for the <c>unsafe-ref-lowering</c> pass.</summary>
public sealed class UnsafeRefLoweringStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary><c>ref T x = ref MemoryMarshal.GetReference(span)</c> declarations lowered.</summary>
    public int RefLocalsLowered { get; set; }

    /// <summary><c>Unsafe.ReadUnaligned&lt;U&gt;(ref x)</c> / <c>(ref Unsafe.Add(ref x, k))</c> sites lowered.</summary>
    public int ReadUnalignedSitesLowered { get; set; }

    /// <summary><c>Unsafe.Add(ref x, k)</c> value reads lowered to <c>span[k]</c>.</summary>
    public int AddValueSitesLowered { get; set; }

    /// <summary>Ref locals preserved verbatim because a reference shape was not provable (URL001).</summary>
    public int RejectedRefLocals { get; set; }
}
