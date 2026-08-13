using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MemoryMarshalReadStructTests
{
    [Fact]
    public void MemoryMarshalRead_StructTarget_AppendsClassArgument()
    {
        var result = Convert(@"
using System.Runtime.InteropServices;

public struct DbRow
{
    public int TokenType;
}

public class Sample
{
    public static DbRow M(System.Span<byte> data)
    {
        return MemoryMarshal.Read<DbRow>(data);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MemoryMarshal.read(", code, StringComparison.Ordinal);
        Assert.Contains(", DbRow.class)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryMarshalRead_IntTarget_NoClassArgument()
    {
        var result = Convert(@"
using System.Runtime.InteropServices;

public class Sample
{
    public static int M(System.Span<byte> data)
    {
        return MemoryMarshal.Read<int>(data);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MemoryMarshal.read(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("int.class", code, StringComparison.Ordinal);
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
