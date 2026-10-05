namespace SampleThreadStaticLib;

/// <summary>
/// Case 2: a thread-static field WITH an inline initializer. Under the default
/// FirstThreadFaithful policy the holder stays parameterless and the initializer
/// moves into a synthesized static constructor, so only the first thread to
/// touch the type observes 42; every later thread observes default(int).
/// </summary>
public static class InitializedCounter
{
    [ThreadStatic] private static int _first = 42;

    public static int Read() => _first;

    public static int Increment() => ++_first;

    public static void Overwrite(int value) => _first = value;
}
