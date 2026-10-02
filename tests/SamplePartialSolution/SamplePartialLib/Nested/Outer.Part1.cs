namespace SamplePartialLib;

/// <summary>Nested partial merge: outer primary part, inner primary part.</summary>
public partial class Outer
{
    public partial class Inner
    {
        public int A() => 1;
    }
}
