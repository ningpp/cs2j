namespace CSharpToJava.Core.PointerDerefIndex;

/// <summary>
/// Options for the independent read-only post-pass <c>pointer-deref-index</c>.
/// The pass runs after <c>unsafe-ffm</c> (and after
/// <c>stackalloc-lowering</c>/<c>ref-struct-lowering</c>) and lowers C# pointer
/// dereferences into pointer element access, which the Java converter already
/// understands (<c>MemorySegment.get</c>).
/// </summary>
public sealed class PointerDerefIndexOptions
{
    /// <summary>
    /// Lower provable pointer indirection <c>*p</c> / <c>*(p + k)</c> /
    /// <c>*(T*)expr</c> into <c>p[0]</c> / <c>p[k]</c> / <c>((T*)expr)[0]</c>.
    /// </summary>
    public bool LowerDereferences { get; init; } = true;
}
