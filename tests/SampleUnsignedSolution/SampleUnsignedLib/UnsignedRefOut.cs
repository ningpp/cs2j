namespace SampleUnsignedLib;

public sealed class UnsignedRefOut
{
    public bool TryParse(string s, out uint result)
    {
        result = 0u;
        return uint.TryParse(s, out result);
    }

    public void Increment(ref uint x) => x++;
}
