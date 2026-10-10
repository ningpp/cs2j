using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Expression;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Lambda Capture Registry integration and LambdaTransformer IIRExpressionTransformer migration.
/// </summary>
public class LambdaCaptureRegistryTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.GeneratedCode;
    }

    // ── Interface check ────────────────────────────────────────────

    [Fact]
    public void LambdaTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(LambdaTransformer.Instance);
    }

    // ── End-to-end mutated capture ─────────────────────────────────

    [Fact]
    public void MutatedCapture_FailsFastWithoutClosureLift()
    {
        // C45 M5: residual mutable capture on the direct pipeline is rejected;
        // lowering belongs to the closure-lift preprocessor (display class).
        var result = ConvertResult(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        act();
    }
}");
        Assert.False(result.Success);
        var message = string.Join(" | ", result.Diagnostics.Select(d => d.Message));
        Assert.Contains("CS2J normalization required", message);
        Assert.Contains("closure-lift", message);
    }

    [Fact]
    public void NonMutatedCapture_NoArrayHolder()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        int x = 5;
        Func<int> f = () => x;
    }
}");
        // Non-mutated capture should NOT produce array holder
        Assert.DoesNotContain("_x", result);
        Assert.DoesNotContain("[0]", result);
    }

    [Fact]
    public void SimpleLambda_EndToEnd()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        Func<int, int> f = x => x * 2;
    }
}");
        Assert.Contains("->", result);
        Assert.Contains("* 2", result);
    }

    [Fact]
    public void BlockBodyLambda_EndToEnd()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        Func<int, int> f = x => {
            int y = x + 1;
            return y;
        };
    }
}");
        Assert.Contains("->", result);
        Assert.Contains("return", result);
    }

    [Fact]
    public void VoidLambdaWithChainedPropertyAssignment_NoBareVariableStatement()
    {
        // When a void lambda's body is a property assignment like () => obj.Prop = value,
        // the converter hoists it to pre-statements and returns a _chainVal temp variable.
        // In a void lambda, the bare _chainVal variable should NOT be emitted as a statement
        // because "_chainVal;" is not a valid Java statement.
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        Action a = () => new Builder().Name = ""test"";
    }
}
class Builder {
    public string Name { get; set; }
}");
        // Should not contain a bare _chainVal; statement inside the lambda
        Assert.DoesNotContain("_chainVal);", result);
    }

    [Fact]
    public void LambdaWithRefArg_MutatedCapture_FailsFastWithoutClosureLift()
    {
        // C45 M5: passing a captured local as ref/out inside the lambda is a
        // mutable capture; direct conversion is rejected pending closure-lift.
        var result = ConvertResult(@"
using System;
class T {
    void M() {
        int index = -1;
        Action a = () => SomeMethod(ref index);
        index = 0;
        SomeMethod(ref index);
    }
    void SomeMethod(ref int i) { }
}");
        Assert.False(result.Success);
        var message = string.Join(" | ", result.Diagnostics.Select(d => d.Message));
        Assert.Contains("CS2J normalization required", message);
        Assert.Contains("closure-lift", message);
    }

    private static ConversionResult ConvertResult(string code)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
