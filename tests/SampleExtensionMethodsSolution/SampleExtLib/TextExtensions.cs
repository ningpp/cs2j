namespace SampleExtLib.Text;

/// <summary>
/// Plain and generic extension methods in a non-global namespace.
/// </summary>
public static class TextExtensions
{
    public static string Shout(this string value)
        => value.ToUpperInvariant() + "!";

    public static string Repeat(this string value, int times)
    {
        var result = string.Empty;
        for (var i = 0; i < times; i++)
        {
            result += value;
        }

        return result;
    }

    /// <summary>Generic extension: call sites normally omit the type argument.</summary>
    public static T Echo<T>(this T value) => value;
}
