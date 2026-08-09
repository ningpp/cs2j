using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for IntPtr (nint) mapping and pointer comparison syntax.
/// Roslyn resolves System.IntPtr as the nint alias, whose display string is
/// "nint" — the TypeMappings.json "System.IntPtr" → "long" entry was therefore
/// never consulted and the raw simple name "IntPtr" leaked into the Java output.
/// Also covers pointer comparisons against constants, which must not append
/// .address() to numeric literals (8.address() is invalid Java).
/// </summary>
public class IntPtrMappingTests
{
    [Fact]
    public void IntPtrLocal_MapsToLong()
    {
        var result = Convert(@"
public class Sample
{
    public long M(int length)
    {
        IntPtr nLength = (IntPtr)length;
        return (long)nLength;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("long nLength = (long)(length);", result.GeneratedCode);
        Assert.DoesNotContain("IntPtr", result.GeneratedCode);
    }

    [Fact]
    public void PointerComparison_WithConstant_DoesNotAppendAddressToLiteral()
    {
        var result = Convert(@"
public unsafe class Sample
{
    public int M(IntPtr nLength)
    {
        while ((byte*)nLength >= (byte*)8)
        {
            nLength -= 8;
        }
        if ((byte*)nLength >= (byte*)4)
        {
            return 1;
        }
        return 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("nLength >= 8", result.GeneratedCode);
        Assert.Contains("nLength >= 4", result.GeneratedCode);
        Assert.DoesNotContain("8.address()", result.GeneratedCode);
        Assert.DoesNotContain("4.address()", result.GeneratedCode);
        Assert.DoesNotContain("IntPtr", result.GeneratedCode);
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
