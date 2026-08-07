using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ++/-- on properties/indexers inside expression-bodied void members
/// (where the expression value is discarded) emit the in-place setter rewrite
/// instead of hoisting a temp whose leftover bare statement is invalid Java.
/// Reproduces RegexParser.PopKeepOptions: <c>void PopKeepOptions() => _optionsStack.Length--;</c>
/// </summary>
public class VoidExpressionBodyPropertyIncrementTests
{
    [Fact]
    public void VoidExpressionBody_PostfixPropertyDecrement_EmitsSetterRewrite()
    {
        var result = Convert(@"
class IntStack {
    public int Length { get; set; }
}

class Parser {
    private IntStack _stack = new IntStack();
    private void PopKeepOptions() => _stack.Length--;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains("setLength", result.GeneratedCode);
        // The discarded-value context must use the in-place setter form, not a
        // hoisted temp left dangling as a bare statement (javac: not a statement).
        Assert.DoesNotContain("_prev", result.GeneratedCode);
    }

    [Fact]
    public void VoidExpressionBody_PrefixPropertyIncrement_EmitsSetterRewrite()
    {
        var result = Convert(@"
class Counter {
    public int Value { get; set; }
}

class Holder {
    private Counter _c = new Counter();
    private void Bump() => ++_c.Value;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains("setValue", result.GeneratedCode);
        Assert.DoesNotContain("_inc", result.GeneratedCode);
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
