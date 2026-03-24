using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that the getMaxParseToken() compatibility rewrite produces max + 1
/// so that the yylex loop <c>while (next >= parserMax)</c> does not swallow
/// legitimate tokens whose value equals the maximum token value (e.g. ID = 134).
/// </summary>
public class ScannerYylexMaxTokenThresholdTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Scanner_YylexThresholdUsesMaxPlusOne()
    {
        // Fixture that mirrors the real GPPG-generated Scanner with both yylex()
        // (which uses parserMax as a skip threshold) and the old reflection-based
        // getMaxParseToken().  Without "+ 1" the loop would eat tokens at the max
        // value (e.g. ID = 134 >= 134 → skipped).
        const string generated = """
            package Dot2Graph;

            import java.io.InputStream;
            import java.util.Arrays;

            public final class Scanner extends ScanBase {
                private int parserMax = getMaxParseToken();

                private static int getMaxParseToken() {
                    Field f = Tokens.class.getField("maxParseToken");
                    return ((Field.valueEquals(f, null) ? Integer.MAX_VALUE : (int)(f.getValue(null))));
                }

                public Scanner(InputStream file) {
                    setSource(file); // no unicode option
                }

                public Scanner() {
                }

                public int yylex() {
                    int next;
                    do {
                        next = scan();
                    } while (next >= parserMax);
                    return next;
                }

                int scan() {
                    switch (state) {
                    case eofNum:
                        if (yywrap()) {
                            return Tokens.EOF.ordinal();
                        }
                        break;
                    case 4, 20:
                        return mkId(getYytext()).ordinal();
                    case 25:
                        return Tokens.ARROW.ordinal();
                    case 26, 28:
                        return Tokens.ID.ordinal();
                    }
                    return 0;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Scanner.java", generated).Replace("\r\n", "\n");

        // The rewrite must produce "+ 1" so that parserMax = max_token_value + 1,
        // making the loop condition (next >= parserMax) false for the highest
        // legitimate token.
        Assert.Contains("return Arrays.stream(Tokens.values()).mapToInt(Tokens::getValue).max().orElse(ScanBuff.EndOfFile) + 1;", output);
        Assert.DoesNotContain("Tokens.class.getField(\"maxParseToken\")", output);
        Assert.DoesNotContain("Field.valueEquals", output);

        // yylex() body itself must remain unchanged — only getMaxParseToken is rewritten
        Assert.Contains("while (next >= parserMax)", output);

        // Other Scanner rewrites should also fire
        Assert.Contains("this.yylval = new ValueType();", output);
        Assert.Contains("return Tokens.EOF.getValue();", output);
        Assert.Contains("return mkId(getYytext()).getValue();", output);
        Assert.DoesNotContain("ordinal()", output);
    }
}
