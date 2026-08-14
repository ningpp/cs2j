namespace SampleUnsignedLib;

public sealed class UnsignedCasts
{
    public uint ToUInt(int x) => (uint)x;
    public ulong ToULong(long x) => (ulong)x;
    public ushort ToUShort(int x) => (ushort)x;
    public byte ToByte(int x) => (byte)x;

    public int ToInt32(uint x) => (int)x;
    public long ToInt64Unsigned(uint x) => (long)x;
    public long ToInt64(ulong x) => (long)x;
    public short ToInt16(ushort x) => (short)x;
    public sbyte ToSByte(byte x) => (sbyte)x;
}
