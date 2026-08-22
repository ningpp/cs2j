using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# discard assignment `_ = expr` inside lambda bodies
/// is correctly converted to just the RHS expression (discarding the
/// value) rather than `_Value = expr()` (which references an undeclared
/// variable in Java).
/// </summary>
public class DiscardAssignmentInLambdaTests
{
    private readonly ITestOutputHelper _out;

    public DiscardAssignmentInLambdaTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DiscardAssignmentInLambda_Body_DoesNotEmit_UndeclaredValue()
    {
        var result = Convert("""
using System;
using System.Collections;

class Test
{
    static void Run()
    {
        var enumerator = new SomeEnumerator();
        Action a = () => _ = enumerator.Current;
    }
}

class SomeEnumerator : IEnumerator
{
    public object Current => new object();
    public bool MoveNext() => false;
    public void Reset() { }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Should NOT contain _Value (which would be an undeclared variable)
        Assert.DoesNotContain("_Value", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscardAssignmentAsStatement_DropsResult()
    {
        var result = Convert("""
class Test
{
    static int SideEffect() => 42;
    static void Run()
    {
        _ = SideEffect();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Should NOT contain _Value
        Assert.DoesNotContain("_Value", code, StringComparison.Ordinal);
        // Should contain the method call (side effect preserved)
        Assert.Contains("sideEffect()", code, StringComparison.Ordinal);
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
