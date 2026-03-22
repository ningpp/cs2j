using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectFileStringsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectFileStrings_RewritesRegexOptionsFlagsToInt()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            import io.github.ningpp.compat.Regex;
            import io.github.ningpp.compat.RegexOptions;

            public class RectFileStrings {
                private static final RegexOptions RgxOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
                public static Regex ParseSeed = new Regex("^Seed\\s+(?<seed>(0x)?\\S+)", RgxOptions);
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectFileStrings.java", generated);

        Assert.Contains("private static final int RgxOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;", output);
        Assert.DoesNotContain("private static final RegexOptions RgxOptions =", output);
    }
}