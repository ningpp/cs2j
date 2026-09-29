using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for balancing parentheses in if-conditions with ternary/null-conditional expressions.
/// C# code like `if (!a[i]?.Equals(b[i]) ?? b[i] != null)` generates extra parens
/// from the combination of NOT (!) + conditional access (?.) + Coalescing (??).
/// </summary>
public class IfBalancingParenTests
{

    [Fact]
    public void SimpleTernaryInIf_NoExtraParens()
    {
        // Simple ternary in if condition should not have extra parens
        var result = Convert(@"
public class SimpleTest
{
    public void M(bool[] arr)
    {
        for (int i = 0; i < arr.Length; ++i)
        {
            if (arr[i] ? true : false) return;
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("if", code);
        Assert.Contains("arr[i]", code);
    }

    [Fact]
    public void IfConditionWithCharLiteralParen_NoExtraClosingParen()
    {
        // C# `if (arr[i] == '(')` contains a char literal whose '(' must not be
        // counted by the paren-balancing pass.  Previously the balancer saw one
        // unmatched '(' (from the char literal) and inserted an extra ')',
        // producing `if (arr[i] == '(')) {` which is invalid Java.
        var result = Convert(@"
public class CharParenTest
{
    public bool Test(char[] arr, int i)
    {
        if (arr[i] == '(')
        {
            return true;
        }
        return false;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The generated if-line must NOT contain the doubled closing paren.
        Assert.DoesNotContain("('))", code, StringComparison.Ordinal);
        Assert.Contains("== '('", code, StringComparison.Ordinal);
    }

    [Fact]
    public void IfConditionWithCharLiteralCloseParen_NoExtraClosingParen()
    {
        // C# `if (arr[i] == ')')` — the char literal ')' contains a ')' that the
        // balancer previously mis-counted as surplus, causing it to strip a '('.
        var result = Convert(@"
public class CharCloseParenTest
{
    public bool Test(char[] arr, int i)
    {
        if (arr[i] == ')')
        {
            return true;
        }
        return false;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("== ')'", code, StringComparison.Ordinal);
        // Must not corrupt the if-condition structure.
        Assert.Contains("if (", code, StringComparison.Ordinal);
        Assert.DoesNotContain(")) {))", code, StringComparison.Ordinal);
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
