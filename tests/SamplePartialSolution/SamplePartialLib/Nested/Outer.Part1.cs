namespace SamplePartialLib;

/// <summary>Nested partial merge: outer primary part, inner primary part.</summary>
public partial class Outer
{
    public partial class Inner
    {
        // M3: classic partial pair nested inside a nested partial type.
        partial void InnerHook();

        public int A() => 1;
    }
}
