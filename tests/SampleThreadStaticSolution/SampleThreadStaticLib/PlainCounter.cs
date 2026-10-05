namespace SampleThreadStaticLib;

/// <summary>
/// Case 1: a thread-static counter WITHOUT an inline initializer. The rewritten
/// holder is constructed parameterless, so every thread starts at default(int).
/// </summary>
public static class PlainCounter
{
    [ThreadStatic] internal static int Hits;

    public static int Increment() => ++Hits;

    public static int Add(int delta) => Hits += delta;

    public static int Current => Hits;

    public static void Reset() => Hits = 0;
}
