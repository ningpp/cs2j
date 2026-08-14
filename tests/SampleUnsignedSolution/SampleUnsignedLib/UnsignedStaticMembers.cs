namespace SampleUnsignedLib;

public sealed class UnsignedStaticMembers
{
    public uint Max => uint.MaxValue;
    public uint Min => uint.MinValue;
    public ulong ULongMax => ulong.MaxValue;
    public uint Parse(string s) => uint.Parse(s);
    public bool TryParse(string s, out uint v) => uint.TryParse(s, out v);
}
