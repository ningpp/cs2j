using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnsignedPrimitiveMappingTests
{
    [Fact]
    public void UnsignedTypes_MapToPrimitiveStorage()
    {
        var result = Convert(@"
class Test {
    byte b = 200;
    ushort u = 42;
    uint ui = 42u;
    ulong ul = 42ul;
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = StripAccessModifiers(result.GeneratedCode);

        Assert.Contains("int b =", code, StringComparison.Ordinal);
        Assert.Contains("200", code, StringComparison.Ordinal);
        Assert.Contains("int u =", code, StringComparison.Ordinal);
        Assert.Contains("int ui =", code, StringComparison.Ordinal);
        Assert.Contains("long ul =", code, StringComparison.Ordinal);
        Assert.Contains("42", code, StringComparison.Ordinal);
        Assert.DoesNotContain("io.github.ningpp.compat.UByte", code, StringComparison.Ordinal);
        Assert.DoesNotContain("io.github.ningpp.compat.UShort", code, StringComparison.Ordinal);
        Assert.DoesNotContain("io.github.ningpp.compat.UInt", code, StringComparison.Ordinal);
        Assert.DoesNotContain("io.github.ningpp.compat.ULong", code, StringComparison.Ordinal);
    }

    private static string StripAccessModifiers(string code)
        => code.Replace("private ", "").Replace("protected ", "").Replace("public ", "");

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
