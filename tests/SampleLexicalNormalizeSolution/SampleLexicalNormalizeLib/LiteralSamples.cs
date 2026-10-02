namespace SampleLexicalNormalizeLib;

/// <summary>
/// C43 fixture samples: every modern lexical form the pass normalizes or
/// skips. Runtime values are asserted by SampleLexicalNormalizeLiteralTests
/// both before and after intermediate-tree normalization.
/// </summary>
public static class LiteralSamples
{
    public const string Single = """hello""";

    public const string Multi = """
        line1
        line2
        """;

    public static ReadOnlySpan<byte> Greeting => "hello中"u8;

    public static byte[] GreetingBytes() => Greeting.ToArray();

    public const string Sql = @"
SELECT *
FROM t
WHERE id = 1";

    public const string EscString = "a\eb";
    public const char EscChar = '\e';

    public static string InterpolatedRaw(int x) => $"""raw {x}""";
    public static string InterpolatedDoubleDollar(int x) => $$"""{{x}}""";
    public static string InterpolatedVerbatim(int x) => $@"a""{x}";
}
