namespace SamplePartialLib;

/// <summary>Ref struct primary part: state, constructor, length.</summary>
public ref partial struct Utf8Reader
{
    private readonly System.ReadOnlySpan<byte> _bytes;

    public Utf8Reader(System.ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes;
    }

    public int Length => _bytes.Length;
}
