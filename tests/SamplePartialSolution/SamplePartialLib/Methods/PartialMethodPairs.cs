namespace SamplePartialLib;

/// <summary>
/// M3 pairing fixtures: a classic (C#3) partial method WITH an in-source
/// implementation, a static classic partial (ref parameter is legal, out is
/// not), and a generic extended (C#9) partial method. Definitions live in the
/// first part; implementations in the second.
/// </summary>
public partial class PartialMethodPairs
{
    partial void Notify(string message);

    static partial void Stamp(ref int value);

    public partial T Choose<T>(T first, T second)
        where T : System.IComparable<T>;

    public string? LastMessage { get; private set; }

    public int Version { get; private set; }

    public void Save() => Notify("saved");

    public static int Mark(int value)
    {
        Stamp(ref value);
        return value;
    }

    public T Pick<T>(T first, T second)
        where T : System.IComparable<T>
        => Choose(first, second);
}

/// <summary>Implementation parts for the three partial method shapes.</summary>
public partial class PartialMethodPairs
{
    partial void Notify(string message)
    {
        LastMessage = message;
        Version++;
    }

    static partial void Stamp(ref int value)
    {
        value *= 2;
    }

    public partial T Choose<T>(T first, T second)
        where T : System.IComparable<T>
        => first.CompareTo(second) >= 0 ? first : second;
}
