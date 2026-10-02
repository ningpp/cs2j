namespace SamplePartialLib;

/// <summary>Ref struct second part: method spanning another folder.</summary>
public ref partial struct Utf8Reader
{
    public byte First => _bytes.IsEmpty ? (byte)0 : _bytes[0];
}
