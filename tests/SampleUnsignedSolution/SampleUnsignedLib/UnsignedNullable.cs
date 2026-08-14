namespace SampleUnsignedLib;

public sealed class UnsignedNullable
{
    public uint? Maybe = null;
    public System.Type TypeOf() => typeof(uint);
    public uint Default() => default(uint);
}
