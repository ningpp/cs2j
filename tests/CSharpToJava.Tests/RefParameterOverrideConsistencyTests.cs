using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A ref parameter on an internal virtual method must render as the same Java
/// holder type on the base declaration and on every override. JsonConverter&lt;T&gt;
/// generated OnTryWrite(WriteStack)/OnTryRead(ReadStack) without the holder while
/// ObjectDefaultConverter etc. overrode with ObjectHolder, silently disabling the
/// overrides and causing JsonResumableConverter read/write self-recursion.
/// </summary>
public class RefParameterOverrideConsistencyTests
{
    [Fact]
    public void InternalVirtualRefParameter_UsesSameHolderInBaseAndOverride()
    {
        var result = Convert(@"
struct Stack { public int X; }

class Base<T>
{
    internal virtual bool OnTryWrite(object writer, T value, object options, ref Stack state)
    {
        return false;
    }
}

class Derived : Base<int>
{
    internal override bool OnTryWrite(object writer, int value, object options, ref Stack state)
    {
        state = new Stack();
        return false;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("onTryWrite(Object writer, T value, Object options, io.github.ningpp.compat.ObjectHolder<Stack> state)", code, StringComparison.Ordinal);
        Assert.Contains("onTryWrite(Object writer, Integer value, Object options, io.github.ningpp.compat.ObjectHolder<Stack> state)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("onTryWrite(Object writer, T value, Object options, Stack state)", code, StringComparison.Ordinal);
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
