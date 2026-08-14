namespace SampleUnsignedLib;

public sealed class UnsignedTypes
{
    public byte ByteField;
    public ushort UShortField;
    public uint UIntField;
    public ulong ULongField;

    public byte ByteProp { get; set; }
    public ushort UShortProp { get; set; }
    public uint UIntProp { get; set; }
    public ulong ULongProp { get; set; }

    public uint Echo(uint value) => value;
    public ulong Echo(ulong value) => value;
    public ushort Echo(ushort value) => value;
    public byte Echo(byte value) => value;
}
