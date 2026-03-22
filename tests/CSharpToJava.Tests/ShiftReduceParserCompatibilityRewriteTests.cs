using CSharpToJava.Core.Pipeline;
using System.Reflection;

namespace CSharpToJava.Tests;

public class ShiftReduceParserCompatibilityRewriteTests
{
    [Fact]
    public void CompatibilityRewrites_FixesExceptionSwitchAndInvalidCharEscapes()
    {
        const string snippet = """
            switch (ex) {
            case :
            return false;
            case :
            return true;
            case :
            num = (this.errorRecovery() ? 1 : 0);
            break;
            default:
            num = 1;
            break;
            }
            switch (input) {
            case MinValue:
            return "'\\0'";
            case '\a':
            return "'\\a'";
            case '\v':
            return "'\\v'";
            }
            protected AcceptException(SerializationInfo info, StreamingContext context) {
            }
            stringBuilder.appendFormat("Syntax error, unexpected {0}", (Object)(nextToken));
            Console.Error.write("State stack is now:");
            Console.Error.write(" {0}", (Object)(state));
            Console.Error.writeLine();
            Console.Error.writeLine("-> {0}", (Object)(symbol));
            return String.format((IFormatProvider)(java.util.Locale.ROOT), "'%s'", (Object)(input));
            super(i, c);
            protected static void yYAccept() {
            throw new AcceptException();
            }
            protected static void yYAbort() {
            throw new AbortException();
            }
            protected static void yYError() {
            throw new ErrorException();
            }
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "ShiftReduceParser.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.DoesNotContain("case :", output);
        Assert.Contains("if (ex instanceof AbortException)", output);
        Assert.Contains("else if (ex instanceof AcceptException)", output);
        Assert.Contains("else if (ex instanceof ErrorException)", output);

        Assert.Contains("case Character.MIN_VALUE:", output);
        Assert.Contains("case '\\u0007':", output);
        Assert.Contains("case '\\u000B':", output);
        Assert.DoesNotContain("case '\\a':", output);
        Assert.DoesNotContain("case '\\v':", output);

        Assert.Contains("AcceptException(Object info, Object context)", output);
        Assert.DoesNotContain("SerializationInfo", output);
        Assert.DoesNotContain("StreamingContext", output);

        Assert.Contains("stringBuilder.append(String.format(", output);
        Assert.Contains("Syntax error, unexpected", output);
        Assert.DoesNotContain("appendFormat(", output);

        Assert.Contains("System.err.printf(\"State stack is now:\")", output);
        Assert.Contains("System.err.printf(\" %s\", (Object)(state))", output);
        Assert.Contains("System.err.println();", output);
        Assert.Contains("System.err.printf(\"-> %s\", (Object)(symbol))", output);
        Assert.DoesNotContain("Console.Error", output);

        Assert.Contains("String.format(\"'%s'\", (Object)(input))", output);
        Assert.DoesNotContain("IFormatProvider", output);
        Assert.Contains("super();", output);
        Assert.DoesNotContain("super(i, c);", output);
        Assert.Contains("protected static void yYAccept() throws AcceptException {", output);
        Assert.Contains("protected static void yYAbort() throws AbortException {", output);
        Assert.Contains("protected static void yYError() throws ErrorException {", output);
    }
}
