using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: a property increment inside a while condition must run on EVERY
/// condition evaluation. The Java emitter lowers property ++/-- to a temp plus
/// pre-statements (<c>var _incN = get(); _incN = _incN + 1; set(_incN);</c>);
/// the while transformer used to drain those pre-statements as a preamble
/// BEFORE the loop, so the increment ran once and the test read the frozen
/// temp — an infinite loop (real chain: XsdDateTime.Parser.parseTime
/// <c>while (++start.Value &lt; _length)</c>, fractional-seconds digits).
/// </summary>
public class WhileConditionPropertyIncrementTests
{
    private readonly ITestOutputHelper _out;

    public WhileConditionPropertyIncrementTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("++")]
    [InlineData("--")]
    public void WhileCondition_PrefixPropertyIncrement_RunsEveryIteration(string op)
    {
        var result = Convert($$"""
            class Counter
            {
                public int Value { get; set; }
            }

            class Test
            {
                public int Count(int limit)
                {
                    var c = new Counter();
                    int n = 0;
                    while ({{op}}c.Value < limit)
                    {
                        n++;
                    }
                    return n;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        _out.WriteLine(code);

        // The condition's read-modify-write must live inside the loop body, in
        // front of the guard; a preamble before `while` would run it once.
        var loopStart = code.IndexOf("while (true)", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "while condition increments must restructure to while (true)\n" + code);

        // The setter call inside the loop body (the class's own `setValue`
        // declaration sits before the loop, so search from loopStart).
        var write = code.IndexOf("setValue(", loopStart, StringComparison.Ordinal);
        Assert.True(write > loopStart,
            "the setter call must be INSIDE the loop body\n" + code);

        var guard = code.IndexOf("if (!(", loopStart, StringComparison.Ordinal);
        Assert.True(guard > loopStart, "the loop-exit test must be inside while (true)\n" + code);
        Assert.True(write < guard,
            "the increment must run before the loop-exit test\n" + code);
    }

    [Theory]
    [InlineData("++")]
    [InlineData("--")]
    public void WhileCondition_PostfixPropertyIncrement_RunsEveryIteration(string op)
    {
        var result = Convert($$"""
            class Counter
            {
                public int Value { get; set; }
            }

            class Test
            {
                public int Count(int limit)
                {
                    var c = new Counter();
                    int n = 0;
                    while (c.Value{{op}} < limit)
                    {
                        n++;
                    }
                    return n;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        _out.WriteLine(code);

        var loopStart = code.IndexOf("while (true)", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "while condition increments must restructure to while (true)\n" + code);

        var write = code.IndexOf("setValue(", loopStart, StringComparison.Ordinal);
        Assert.True(write > loopStart,
            "the setter call must be INSIDE the loop body\n" + code);

        // Postfix must still test the OLD value (read before the write).
        var oldRead = code.IndexOf("getValue()", loopStart, StringComparison.Ordinal);
        Assert.True(oldRead > loopStart && oldRead < write,
            "postfix must read the old value before writing\n" + code);
        Assert.Contains("if (!(_inc", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
