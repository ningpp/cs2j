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
                protected void doAction(int action) {
                    switch (action) {
                    }
                }

                protected void initialize() {
                    this.initSpecialTokens(Tokens.error.ordinal(), Tokens.EOF.ordinal());
                }

                protected String terminalToString(int terminal) {
                    if (!java.util.Objects.equals((Tokens.values()[(int)(terminal)]).toString(), String.valueOf(terminal))) {
                        return (Tokens.values()[(int)(terminal)]).toString();
                    } else {
                        return charToString((char)(terminal));
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Parser.java", generated);

        Assert.Contains("if (CurrentSemanticValue == null)", output);
        Assert.Contains("CurrentSemanticValue = new ValueType();", output);
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

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Scanner_UsesUnsignedIndexForTransitionTable()
    {
        const string generated = """
            package Dot2Graph;

            public final class Scanner extends ScanBase {
                static class Table {
                    int min;
                    int rng;
                    int dflt;
                    byte[] nxt;
                }

                int code;
                int state;
                static Table[] NxS;

                int nextState() {
                    int rslt;
                    int idx = (byte)((code - NxS[state].min));
                    if ((int)(idx) >= (int)(NxS[state].rng)) {
                        rslt = NxS[state].dflt;
                    } else {
                        rslt = NxS[state].nxt[idx];
                    }
                    return rslt;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Scanner.java", generated).Replace("\r\n", "\n");

        Assert.Contains("int unsignedIdx = Byte.toUnsignedInt((byte)idx);", output);
        Assert.Contains("if (unsignedIdx >= NxS[state].rng)", output);
        Assert.Contains("rslt = NxS[state].nxt[unsignedIdx];", output);
        Assert.DoesNotContain("NxS[state].nxt[idx];", output);
        Assert.DoesNotContain("if ((int)(idx) >= (int)(NxS[state].rng))", output);
    }
}