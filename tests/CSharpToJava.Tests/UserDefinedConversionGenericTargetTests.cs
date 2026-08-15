using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for user-defined conversion operators whose target type is generic
/// (e.g. implicit operator ReadOnlyMemory&lt;T&gt;(T[]) from the compat memory
/// types). The old "to&lt;TargetType&gt;" static-helper synthesis produced invalid
/// Java such as ReadOnlyMemory&lt;Integer&gt;.toReadOnlyMemory&lt;Integer&gt;(...);
/// generic targets must instead be wrapped with the compat constructor.
/// </summary>
public class UserDefinedConversionGenericTargetTests
{
    [Fact]
    public void ArrayToReadOnlyMemory_ImplicitConversion_UsesConstructor()
    {
        var result = Convert(@"
public class Sample
{
    public void M(byte[] data)
    {
        System.ReadOnlyMemory<byte> segment = data;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new ReadOnlyMemory<io.github.ningpp.compat.UByte>(data)", result.GeneratedCode);
        Assert.DoesNotContain("toReadOnlyMemory", result.GeneratedCode);
        Assert.DoesNotContain("ReadOnlyMemory<io.github.ningpp.compat.UByte>.ReadOnlyMemory", result.GeneratedCode);
    }

    [Fact]
    public void ArrayToReadOnlySpan_ExplicitCast_UsesConstructor()
    {
        var result = Convert(@"
public class Sample
{
    public void M(byte[] data)
    {
        System.ReadOnlySpan<byte> span = (System.ReadOnlySpan<byte>)data;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new ReadOnlySpan<io.github.ningpp.compat.UByte>(data)", result.GeneratedCode);
        Assert.DoesNotContain("toReadOnlySpan", result.GeneratedCode);
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
