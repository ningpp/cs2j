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

    // ── Lambda with ref parameter: post-statements must stay inside lambda ──

    [Fact]
    public void LambdaWithRefParam_PostStatementsInsideLambda()
    {
        // When a lambda expression body contains a ref parameter (e.g., ref index),
        // the converter generates IntHolder pre/post statements. The post-statement
        // (index = _indexRef.value) must be placed INSIDE the lambda body, not outside,
        // because _indexRef is declared inside the lambda.
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        int index = 0;
        Action a = () => SomeMethod(ref index);
        index = 5;
    }
    void SomeMethod(ref int i) { }
}");
        // The _indexRef variable should be declared inside the lambda,
        // and the post-statement assigning back should also be inside the lambda.
        // We verify that _indexRef is NOT referenced outside the lambda body.
        Assert.Contains("IntHolder", result);
        // Check that _indexRef is not used outside the lambda (would cause compile error)
        // The lambda should be a block lambda with the holder declaration inside
        Assert.Contains("-> {", result);
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
    public void LambdaWithRefParam_MutatedCaptureUsesHolderInLambda()
    {
        // When a variable is passed as ref inside a lambda AND is reassigned outside the lambda,
        // the lambda body should use _index[0] instead of the raw 'index' variable,
        // because Java requires lambda-captured variables to be effectively final.
        var result = ConvertCode(@"
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
        // index inside the lambda should be replaced with _index[0]
        // to satisfy Java's effectively-final requirement
        Assert.Contains("_index", result);
        // The lambda body should NOT reference raw 'index' directly
        // (it should use _index[0] instead)
        Assert.DoesNotContain("IntHolder _indexRef = new IntHolder(index)", result);
    }

    [Fact]
    public void RefCallOutsideLambda_UsesCaptureHolderNotRawVariable()
    {
        // When a variable is captured by a lambda (creating int[] _index = { index })
        // and then passed as ref OUTSIDE the lambda, the ref holder should be
        // seeded from _index[0] (not the raw 'index' variable which may be stale).
        var result = ConvertCode(@"
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
        // The ref call outside the lambda should use _index[0], not raw 'index'
        Assert.DoesNotContain("new IntHolder(index)", result);
        Assert.Contains("new IntHolder(_index[0])", result);
    }
}
