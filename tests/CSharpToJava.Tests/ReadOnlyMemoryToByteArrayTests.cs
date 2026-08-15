using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ReadOnlyMemory&lt;byte&gt;.ToArray() maps to the compat
/// ReadOnlyMemory.toByteArray() (byte[] result), and that the implicit
/// byte[] → ReadOnlyMemory&lt;byte&gt; conversion wraps the array in
/// new ReadOnlyMemory&lt;Integer&gt;(byte[]). The compat library must provide
/// both APIs for the generated code to compile.
/// </summary>
public class ReadOnlyMemoryToByteArrayTests
{
    [Fact]
    public void ToArrayOnReadOnlyMemoryOfByte_EmitsToByteArray()
    {
        var result = Convert(@"
class Holder {
    public byte[] Dump(System.ReadOnlyMemory<byte> m) => m.ToArray();
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ReadOnlyMemory<io.github.ningpp.compat.UByte>", code);
        Assert.Contains("m.toByteArray()", code);
    }

    [Fact]
    public void ImplicitByteArrayToReadOnlyMemory_EmitsConstructorWrap()
    {
        var result = Convert(@"
class Holder {
    public System.ReadOnlyMemory<byte> Wrap(byte[] data) => data;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new ReadOnlyMemory<io.github.ningpp.compat.UByte>(data)", code);
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
