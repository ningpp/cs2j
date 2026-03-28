using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TestFileReaderCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_TestFileReader_RewritesStringComparisonAndUnsignedParsing()
    {
        // StringComparison arguments are now handled by InvocationExpressionTransformer,
        // so the post-processor input already has StringHelper calls.
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            public class TestFileReader {
                void load(String currentLine, String strArg, String strFixedPos) {
                    if (StringHelper.startsWith(currentLine, "//", true)) {
                    }
                    int style = System.Globalization.NumberStyles.Integer;
                    if (StringHelper.startsWith(strArg, "0x", true)) {
                        strArg = strArg.substring(2);
                        style = System.Globalization.NumberStyles.HexNumber;
                    }
                    this.setSeed(Integer.parseInt(strArg, style));
                    if (0 == StringHelper.compare("NewHierarchy", currentLine, true)) {
                    }
                    if (0 == StringHelper.compare("Fixed", strFixedPos, true)) {
                    }
                    var value = uint.parseUint("FF", 16);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("TestFileReader.java", generated).Replace("\r\n", "\n");

        Assert.Contains("StringHelper.startsWith(currentLine, \"//\", true)", output);
        Assert.Contains("int radix = 10;", output);
        Assert.Contains("radix = 16;", output);
        Assert.Contains("this.setSeed(radix == 16 ? Integer.parseUnsignedInt(strArg, radix) : Integer.parseInt(strArg, radix));", output);
        Assert.Contains("StringHelper.compare(\"NewHierarchy\", currentLine, true)", output);
        Assert.Contains("StringHelper.compare(\"Fixed\", strFixedPos, true)", output);
        Assert.Contains("Integer.parseUnsignedInt(\"FF\", 16)", output);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", output);
        Assert.DoesNotContain("System.Globalization.NumberStyles", output);
        Assert.DoesNotContain("uint.parseUint", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_TestFileReader_RewritesFileReaderCheckedException()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import java.io.*;

            public class TestFileReader {
                public void load(String strFullName) {
                    try (BufferedReader sr = new BufferedReader(new FileReader(strFullName))) {
                        String currentLine;
                    } // end using sr
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("TestFileReader.java", generated).Replace("\r\n", "\n");

        Assert.Contains("BufferedReader sr = null;", output);
        Assert.Contains("sr = new BufferedReader(new FileReader(strFullName));", output);
        Assert.Contains("} catch (IOException e) {", output);
        Assert.Contains("throw new RuntimeException(e);", output);
        Assert.Contains("sr.close();", output);
        Assert.DoesNotContain("try (BufferedReader sr = new BufferedReader(new FileReader(strFullName)))", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_TestFileReader_FileReaderRewriteIsIdempotent()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import java.io.*;

            public class TestFileReader {
                public void load(String strFullName) {
                    try (BufferedReader sr = new BufferedReader(new FileReader(strFullName))) {
                        String currentLine;
                    } // end using sr
                }
            }
            """;

        var once = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("TestFileReader.java", generated).Replace("\r\n", "\n");
        var twice = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("TestFileReader.java", once).Replace("\r\n", "\n");

        Assert.Equal(once, twice);
        Assert.Equal(1, CountOccurrences(twice, "catch (IOException e)"));
        Assert.Equal(1, CountOccurrences(twice, "finally {"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
