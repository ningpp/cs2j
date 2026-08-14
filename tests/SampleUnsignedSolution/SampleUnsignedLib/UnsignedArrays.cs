namespace SampleUnsignedLib;

public sealed class UnsignedArrays
{
    public uint[] Arr = new uint[10];
    public uint[,] Matrix = new uint[3, 4];
    public uint[][] Jagged = new uint[2][];
    public byte[] Bytes = new byte[] { 1, 2 };

    public void Fill()
    {
        Arr[0] = 5u;
        Matrix[0, 0] = 1u;
        Jagged[0] = new uint[] { 1u, 2u };
    }
}
