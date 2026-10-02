namespace SamplePartialLib;

/// <summary>Nested partial merge: outer second part, inner second part.</summary>
public partial class Outer
{
    public partial class Inner
    {
        public int B() => 2;
    }
}
