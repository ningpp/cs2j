using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# (ushort) cast expressions are correctly converted to Java.
/// In C#, (ushort) is used to truncate values to 16 bits (unsigned). Converting
/// (ushort) to (short) in Java is incorrect because:
/// 1. (short) produces a short, not an int, causing type incompatibility when
///    assigning to int variables (e.g. int idx = (short)(flags & IndexMask))
/// 2. (short) preserves sign, losing unsigned semantics
/// The correct conversion should use &amp; 0xFFFF masking (result is int),
/// consistent with how (byte) is converted to &amp; 0xFF.
/// </summary>
public class UShortCastConversionTests
{
    [Fact]
    public void UShortCast_FlagsMaskAssignment_DoesNotProduceShortCast()
    {
        var result = Convert(@"
class Test {
    void M(long flags) {
        ushort idx = (ushort)(flags & 0x0000FFFF);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (ushort) cast must NOT be converted to (short) cast
        Assert.DoesNotContain("(short)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortCast_FlagsMaskAssignment_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M(long flags) {
        ushort idx = (ushort)(flags & 0x0000FFFF);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The generated Java should use & 0xFFFF masking (result is int)
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortCast_SimpleValue_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M(int value) {
        ushort result = (ushort)value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (ushort)value should become (value) & 0xFFFF, not (short)(value)
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(short)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortCast_LengthProperty_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M(string s) {
        ushort length = (ushort)s.Length;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (ushort)s.Length should use & 0xFFFF masking
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(short)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortCast_InComparison_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    bool M(long flags) {
        return (ushort)(flags & 0xFFFF) < 10;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (ushort) in comparison should use & 0xFFFF
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(short)", result.GeneratedCode, StringComparison.Ordinal);
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
