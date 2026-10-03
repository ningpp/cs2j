namespace SampleExtLib.Delegates;

/// <summary>C19 M2: extension captured as a method-group delegate.</summary>
public static class GreetingExtensions
{
    public static int GreetingCount;

    public static void Greet(this string name) => GreetingCount++;
}
