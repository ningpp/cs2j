namespace SamplePartialLib;

/// <summary>Nested partial merge: outer second part, inner second part.</summary>
public partial class Outer
{
    public partial class Inner
    {
        private int _hookCount;

        partial void InnerHook()
        {
            _hookCount++;
        }

        public int B() => 2;

        public int C()
        {
            InnerHook();
            return _hookCount + 2;
        }
    }
}
