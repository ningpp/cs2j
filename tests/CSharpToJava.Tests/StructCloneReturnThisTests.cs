using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for struct methods that return <c>this</c>.
/// </summary>
public class StructCloneReturnThisTests
{
    [Fact]
    public void CloneMethod_ReturnThis_StaysThis_NotRecursive()
    {
        var result = Convert(@"
public struct Node
{
    public bool IsDisposable;
    public Node Clone()
    {
        if (!IsDisposable)
        {
            return this;
        }
        return new Node();
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        // C# Clone() maps to Java clone(); returning `this` inside clone() must
        // NOT become `this.clone()`, otherwise the method recurses forever.
        Assert.Contains("public Node clone()", code, StringComparison.Ordinal);
        Assert.Contains("return this;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return this.clone();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCloneMethod_ReturnThis_StillClones()
    {
        var result = Convert(@"
public struct Node
{
    public int X;
    public Node Self()
    {
        return this;
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        // Value-copy semantics still apply in ordinary methods: return this → this.clone().
        Assert.Contains("public Node self()", code, StringComparison.Ordinal);
        Assert.Contains("return this.clone();", code, StringComparison.Ordinal);
    }

    private static Core.Pipeline.ConversionResult Convert(string sourceCode)
    {
        var pipeline = new Core.Pipeline.ConversionPipeline();
        return pipeline.Convert(new Core.Pipeline.ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new Core.Context.ConversionOptions(),
        });
    }
}
