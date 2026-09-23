using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnsafeReadUnalignedVectorTests
{

    [Fact]
    public void ReadUnaligned_IntTarget_StaysReadUnaligned()
    {
        var result = Convert(@"
using System.Runtime.CompilerServices;

public class Sample
{
    public static int M(io.github.ningpp.compat.IntHolder holder)
    {
        return Unsafe.ReadUnaligned<int>(holder);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("readUnalignedVector", code, StringComparison.Ordinal);
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
