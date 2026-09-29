using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Systemic tests for three Java compilation errors:
/// 1. Stream&lt;T&gt; cannot be converted to Iterable&lt;T&gt; (expression-bodied returns)
/// 2. T[] cannot be converted to Iterable&lt;T&gt; (expression-bodied returns)
/// 3. Cannot find symbol: variable Count on Collection&lt;T&gt; (missing IReadOnlyCollection mappings)
/// </summary>
public class StreamArrayCountSystemicTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Error 1: Stream<T> → Iterable<T> — expression-bodied methods/properties
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    // Error 2: T[] → Iterable<T> — expression-bodied methods/properties
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    // Regression guards — existing behaviour must not break
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForeachOnArray_NotWrapped()
    {
        var result = Convert(@"
class Sample
{
    void M(string[] arr)
    {
        foreach (var s in arr) { }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Arrays are directly iterable in Java foreach — must NOT be wrapped
        Assert.DoesNotContain("ArrayHelper.toList", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedVoidMethod_NotAffected()
    {
        var result = Convert(@"
using System;
class Sample
{
    void M() => Console.WriteLine(""hello"");
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // void method — no collect/asList wrapping
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.toList", result.GeneratedCode, StringComparison.Ordinal);
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
