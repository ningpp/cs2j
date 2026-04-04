using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# ternary expressions with assignment in the false branch
/// emit properly parenthesized Java code. Without parentheses, Java parses
/// `a != null ? a : a = expr` as `(a != null ? a : a) = expr`, which is invalid.
/// </summary>
public class TernaryAssignmentParensTests
{
    [Fact]
    public void ConditionalExpression_AssignmentInFalseBranch_EmitsParens()
    {
        var result = Convert(@"
class Foo
{
    Foo _root;
    Foo ComputeRoot() => new Foo();
    Foo GetRoot() => _root != null ? _root : _root = ComputeRoot();
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The assignment in the false branch must be parenthesized
        Assert.Contains("(_root = computeRoot())", result.GeneratedCode);
        // Must NOT have the unparenthesized form
        Assert.DoesNotContain(": _root = computeRoot()", result.GeneratedCode);
    }

    [Fact]
    public void NullCoalesce_WithAssignment_AlreadyCorrect()
    {
        var result = Convert(@"
class Foo
{
    Foo _cached;
    Foo ComputeValue() => new Foo();
    Foo GetCachedOrCompute() => _cached ?? (_cached = ComputeValue());
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Null-coalescing with parenthesized assignment should keep parens
        Assert.Contains("(_cached = computeValue())", result.GeneratedCode);
    }

    [Fact]
    public void ConditionalExpression_NoAssignment_NoExtraParens()
    {
        var result = Convert(@"
class Test
{
    string _a;
    string _b;
    string Get() => _a != null ? _a : _b;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("_a != null ? _a : _b", result.GeneratedCode);
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
