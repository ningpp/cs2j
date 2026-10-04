namespace SampleExtensionMembers;

// Classic C# extension methods coexist with extension blocks.
public static class ClassicExtensions
{
    public static int Square(this int x) => x * x;

    public static string Shout(this string s) => s.ToUpperInvariant() + "!";
}
