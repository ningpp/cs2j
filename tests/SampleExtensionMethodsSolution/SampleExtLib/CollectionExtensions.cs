namespace SampleExtLib.Collections;

/// <summary>
/// Enumerable receiver extensions, including a null-guard on the receiver.
/// </summary>
public static class CollectionExtensions
{
    public static IEnumerable<T> RequireNonEmpty<T>(this IEnumerable<T>? source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (!source.Any())
        {
            throw new ArgumentException("sequence must not be empty", nameof(source));
        }

        return source;
    }

    public static string JoinWith(this IEnumerable<string> source, string separator)
        => string.Join(separator, source);
}
