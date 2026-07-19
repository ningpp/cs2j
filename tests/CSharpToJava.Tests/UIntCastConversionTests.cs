using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# (uint) cast expressions are correctly converted to Java.
/// In C#, (uint) is used for unsigned comparison tricks (e.g. checking if a
/// value is within a range by relying on unsigned wrap-around). Converting
/// (uint) to (int) in Java is incorrect because (int) preserves the sign.
/// The correct conversion should use &amp; 0xFFFFFFFFL masking or
/// Integer.compareUnsigned.
/// </summary>
public class UIntCastConversionTests
{
    [Fact]
    public void UIntCast_SubtractionComparison_DoesNotProduceSignedIntCast()
    {
        var result = Convert(@"
class Test {
    bool IsHexDigit(char character) =>
        (uint)(character - '0') <= '9' - '0' ||
        (uint)(character - 'A') <= 'F' - 'A' ||
        (uint)(character - 'a') <= 'f' - 'a';
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (uint) cast must NOT be converted to (int) cast, which would be wrong
        // for unsigned comparison patterns.
        Assert.DoesNotContain("(int)((character - '0'))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(int)((character - 'A'))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(int)((character - 'a'))", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UIntCast_SubtractionComparison_UsesUnsignedPattern()
    {
        var result = Convert(@"
class Test {
    bool IsHexDigit(char character) =>
        (uint)(character - '0') <= '9' - '0' ||
        (uint)(character - 'A') <= 'F' - 'A' ||
        (uint)(character - 'a') <= 'f' - 'a';
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The generated Java should use an unsigned comparison pattern.
        // Acceptable patterns: & 0xFFFFFFFFL masking or Integer.compareUnsigned
        var hasMask = result.GeneratedCode.Contains("0xFFFFFFFFL", StringComparison.Ordinal);
        var hasCompareUnsigned = result.GeneratedCode.Contains("compareUnsigned", StringComparison.Ordinal);
        Assert.True(hasMask || hasCompareUnsigned,
            "Expected generated Java to use either '& 0xFFFFFFFFL' masking or 'Integer.compareUnsigned' " +
            "for (uint) cast conversion, but neither was found.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void UIntCast_SimpleRangeCheck_DoesNotProduceSignedIntCast()
    {
        var result = Convert(@"
class Test {
    bool CheckRange(int value) => (uint)value <= 10;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (uint)value must NOT become (int)(value)
        Assert.DoesNotContain("(int)(value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UIntCast_SimpleRangeCheck_UsesUnsignedPattern()
    {
        var result = Convert(@"
class Test {
    bool CheckRange(int value) => (uint)value <= 10;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var hasMask = result.GeneratedCode.Contains("0xFFFFFFFFL", StringComparison.Ordinal);
        var hasCompareUnsigned = result.GeneratedCode.Contains("compareUnsigned", StringComparison.Ordinal);
        Assert.True(hasMask || hasCompareUnsigned,
            "Expected generated Java to use either '& 0xFFFFFFFFL' masking or 'Integer.compareUnsigned' " +
            "for (uint) cast conversion, but neither was found.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void UIntCast_ObjectValue_UsesIntegerUnboxingMask()
    {
        var result = Convert(@"
class Test {
    double M(object value) {
        return (double)(uint)value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // (uint)obj must unbox Object to Integer before applying the uint mask;
        // Object cannot be used directly as an operand of bitwise AND with a long literal.
        Assert.Contains("(Integer)(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFFFFFFL", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(value) & 0xFFFFFFFFL", result.GeneratedCode, StringComparison.Ordinal);
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
