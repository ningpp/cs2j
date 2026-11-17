using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for emitting the untyped recursive pattern `x is { } t`
/// (non-null test plus binding) as a Java 16+ `instanceof` pattern. The
/// null-coalescing/null-conditional preprocessor deliberately produces this
/// shape to evaluate a side-effecting operand exactly once; the emitter used
/// to fall through to `/* TODO: unsupported pattern */ true` and never declare
/// the pattern variable (real chain MSAGL SplineRouter: `___t1` not found).
/// </summary>
public class NonNullPatternEmitTests
{
    [Fact]
    public void Simple_NonNullPattern_Emits_Instanceof_Pattern()
    {
        var result = Convert(@"
class Widget { }
class Sample {
    Widget M(Widget w) {
        return w is { } t ? t : new Widget();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        Assert.Contains("instanceof Widget t", code, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported pattern", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_NonNullPattern_Emits_Erased_Instanceof_Target()
    {
        // Generic operands must erase type arguments: `instanceof CSharpList<Integer>`
        // is not legal Java, the raw host class is.
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    List<int> M(List<int> list) {
        List<int> v = list is { } t ? t : new List<int>();
        return v;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        Assert.Contains("instanceof CSharpList t", code, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof CSharpList<", code, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported pattern", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
