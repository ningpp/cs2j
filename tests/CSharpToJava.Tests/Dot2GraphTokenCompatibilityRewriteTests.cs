using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class Dot2GraphTokenCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Parser_UsesTokenValuesInsteadOfOrdinals()
    {
        const string generated = """
            package Dot2Graph;

            public class Parser {
                protected void initialize() {
                    this.initSpecialTokens(Tokens.error.ordinal(), Tokens.EOF.ordinal());
                }

                protected String terminalToString(int terminal) {
                    if (!((Tokens.values()[(int)(terminal)]).toString().equals(String.valueOf(terminal)))) {
                        return (Tokens.values()[(int)(terminal)]).toString();
                    } else {
                        return charToString((char)(terminal));
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Parser.java", generated);

        Assert.Contains("this.initSpecialTokens(Tokens.error.getValue(), Tokens.EOF.getValue());", output);
        Assert.Contains("Arrays.stream(Tokens.values()).filter(token -> token.getValue() == terminal)", output);
        Assert.DoesNotContain("Tokens.values()[(int)(terminal)]", output);
        Assert.DoesNotContain("ordinal()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Scanner_InitializesYylvalAndReturnsTokenValues()
    {
        const string generated = """
            package Dot2Graph;

            public final class Scanner extends ScanBase {
                private static int getMaxParseToken() {
                    Field f = Tokens.class.getField("maxParseToken");
                    return ((Field.valueEquals(f, null) ? Integer.MAX_VALUE : (int)(f.getValue(null))));
                }

                public Scanner(InputStream file) {
                    setSource(file); // no unicode option
                }

                public Scanner() {
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

        Assert.Contains("return Arrays.stream(Tokens.values()).mapToInt(Tokens::getValue).max().orElse(ScanBuff.EndOfFile) + 1;", output);
        Assert.Contains("this.yylval = new ValueType();", output);
        Assert.Contains("return Tokens.EOF.getValue();", output);
        Assert.Contains("return mkId(getYytext()).getValue();", output);
        Assert.Contains("return Tokens.ARROW.getValue();", output);
        Assert.Contains("return Tokens.ID.getValue();", output);
        Assert.DoesNotContain("ordinal()", output);
    }
}