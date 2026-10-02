namespace SampleInterpolationLib;

/// <summary>
/// IE-FOLD constant site, a nested interpolation (inner expands first), a
/// verbatim interpolation and a single-line raw interpolation: products
/// carry no raw/verbatim lexicon.
/// </summary>
public static class FoldNestedRawSites
{
    public static string Fold()
    {
        const string w = "W";
        // The interpolation folds to "ab-W"; the trailing w keeps the const
        // local used so the intermediate build stays warning-free.
        var folded = $"a{"b"}-{w}";
        return folded + w;
    }

    public static string Nested(int n)
        => $"a{$"b{n}"}";

    public static string VerbatimPath(int id)
        => $@"C:\tmp\{id}.txt";

    public static string RawQuote(int id)
        => $"""a"{id}b""";
}
