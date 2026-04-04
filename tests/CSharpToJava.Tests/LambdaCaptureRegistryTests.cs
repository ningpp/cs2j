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

    // ── CaptureInfo record ─────────────────────────────────────────

    [Fact]
    public void CaptureInfo_HasCorrectProperties()
    {
        var info = new MethodConversionState.CaptureInfo("x", "int", true);
        Assert.Equal("x", info.VarName);
        Assert.Equal("int", info.JavaType);
        Assert.True(info.IsMutable);
    }

    [Fact]
    public void CaptureInfo_EqualityByValue()
    {
        var a = new MethodConversionState.CaptureInfo("x", "int", true);
        var b = new MethodConversionState.CaptureInfo("x", "int", true);
        Assert.Equal(a, b);
    }

    // ── MethodConversionState registry ─────────────────────────────

    [Fact]
    public void RegisterLambdaCaptures_ThenTryGet_ReturnsCaptures()
    {
        var state = new MethodConversionState();
        var captures = new List<MethodConversionState.CaptureInfo>
        {
            new("x", "int", true),
            new("y", "String", false)
        };
        state.RegisterLambdaCaptures("lambda1", captures);
        
        Assert.True(state.TryGetLambdaCaptures("lambda1", out var result));
        Assert.Equal(2, result.Count);
        Assert.Equal("x", result[0].VarName);
        Assert.Equal("y", result[1].VarName);
    }

    [Fact]
    public void TryGetLambdaCaptures_UnknownKey_ReturnsFalse()
    {
        var state = new MethodConversionState();
        Assert.False(state.TryGetLambdaCaptures("unknown", out _));
    }

    [Fact]
    public void Reset_ClearsLambdaCaptureRegistry()
    {
        var state = new MethodConversionState();
        state.RegisterLambdaCaptures("key", new List<MethodConversionState.CaptureInfo>
        {
            new("x", "int", true)
        });
        state.Reset();
        Assert.False(state.TryGetLambdaCaptures("key", out _));
    }

    // ── End-to-end mutated capture ─────────────────────────────────

    [Fact]
    public void MutatedCapture_ProducesArrayHolder()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        act();
    }
}");
        // Mutated capture should produce array holder: int[] _count = { count };
        Assert.Contains("_count", result);
        Assert.Contains("[0]", result);
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
    public void AsyncLambda_ProducesCompletableFuture()
    {
        var result = ConvertCode(@"
using System;
using System.Threading.Tasks;
class T {
    void M() {
        Func<Task<int>> f = async () => {
            return 42;
        };
    }
}");
        Assert.Contains("CompletableFuture", result);
    }
}
