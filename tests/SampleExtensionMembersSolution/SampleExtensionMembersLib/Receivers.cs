namespace SampleExtensionMembers;

/// <summary>Receiver types extended by the blocks in BlockHosts.</summary>
public sealed class Box<T>
{
    public T Value;

    public Box(T value) => Value = value;
}

public struct Vec
{
    public int X;
    public int Y;

    public Vec(int x, int y)
    {
        X = x;
        Y = y;
    }
}

public struct Num
{
    public int Value;

    public Num(int value) => Value = value;
}
