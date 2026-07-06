using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# numeric literals to Java literals.
/// Most literal forms are valid in both languages and pass through; the converter
/// must not leave C#-specific suffixes (e.g. 'm' for decimal) in the output.
/// </summary>
public class NumericLiteralTests : ConversionTestBase
{
    [Fact]
    public void DecimalIntegerLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 12345; } }");
        AssertConversion(result, "return 12345;");
    }

    [Fact]
    public void LongSuffixLiteral_MapsToJavaLongSuffix()
    {
        var result = Convert("class C { public long M() { return 123L; } }");
        AssertConversion(result, "return 123L;");
    }

    [Fact]
    public void DoubleLiteral_PassesThrough()
    {
        var result = Convert("class C { public double M() { return 3.14159; } }");
        AssertConversion(result, "return 3.14159;");
    }

    [Fact]
    public void FloatSuffixLiteral_MapsToJavaFloatSuffix()
    {
        var result = Convert("class C { public float M() { return 2.5f; } }");
        AssertConversion(result, "return 2.5f;");
    }

    [Fact]
    public void HexLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 0x1F; } }");
        AssertConversion(result, "return 0x1F;");
    }

    [Fact]
    public void BinaryLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 0b1010; } }");
        AssertConversion(result, "return 0b1010;");
    }

    [Fact]
    public void UnderscoreLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 1_000_000; } }");
        AssertConversion(result, "return 1_000_000;");
    }

    [Fact]
    public void NegativeLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return -42; } }");
        AssertConversion(result, "return -42;");
    }

    [Fact]
    public void UnsignedLongSuffixLiteral_MapsToJavaLong()
    {
        var result = Convert("class C { public ulong M() { return 10UL; } }");
        AssertConversion(result, "return 10L;");
    }

    [Fact]
    public void UnsignedIntSuffixLiteral_MapsToJavaInt()
    {
        var result = Convert("class C { public uint M() { return 7U; } }");
        AssertConversion(result, "return 7;");
    }

    [Fact]
    public void DecimalSuffixLiteral_MapsToDecimalValue()
    {
        var result = Convert("class C { public decimal M() { return 1.25m; } }");
        AssertConversion(result, "import io.github.ningpp.compat.Decimal;");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "1.25m", "decimal 'm' suffix must be removed");
    }

    [Fact]
    public void CharHexEscape_PassesThrough()
    {
        var result = Convert("class C { public char M() { return '\\x41'; } }");
        AssertConversion(result, "return 'A';");
    }

    [Fact]
    public void CharUnicodeEscape_PassesThrough()
    {
        var result = Convert("class C { public char M() { return '\\u0041'; } }");
        AssertConversion(result, "return 'A';");
    }

    [Fact]
    public void ZeroLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 0; } }");
        AssertConversion(result, "return 0;");
    }

    [Fact]
    public void MaxIntLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 2147483647; } }");
        AssertConversion(result, "return 2147483647;");
    }

    [Fact]
    public void DoubleScientificNotation_PassesThrough()
    {
        var result = Convert("class C { public double M() { return 1e3; } }");
        AssertConversion(result, "return 1e3;");
    }

    [Fact]
    public void LongMaxLiteral_PassesThrough()
    {
        var result = Convert("class C { public long M() { return 9000000000L; } }");
        AssertConversion(result, "return 9000000000L;");
    }

    [Fact]
    public void LiteralInFieldInitializer_PassesThrough()
    {
        var result = Convert("class C { public const double Tolerance = 0.001; }");
        AssertConversion(result, "public static final double Tolerance = 0.001;");
    }

    [Fact]
    public void ByteValueLiteral_MapsToInt()
    {
        var result = Convert("class C { public byte M() { return 255; } }");
        AssertConversion(result, "public int m()", "return (255) & 0xFF;");
    }

    [Fact]
    public void ShortValueLiteral_PassesThrough()
    {
        var result = Convert("class C { public short M() { return 32000; } }");
        AssertConversion(result, "return (short) (32000);");
    }

    [Fact]
    public void BooleanLiteralTrue_False_PassesThrough()
    {
        var result = Convert("class C { public bool M() { return true; } }");
        AssertConversion(result, "return true;");
    }

    [Fact]
    public void SumOfLiterals_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 1 + 2 + 3; } }");
        AssertConversion(result, "return 1 + 2 + 3;");
    }

    [Fact]
    public void MultiplicationOfLiterals_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 6 * 7; } }");
        AssertConversion(result, "return 6 * 7;");
    }

    [Fact]
    public void ShiftLeftLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 1 << 4; } }");
        AssertConversion(result, "return 1 << 4;");
    }

    [Fact]
    public void ShiftRightLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 16 >> 2; } }");
        AssertConversion(result, "return 16 >> 2;");
    }

    [Fact]
    public void BitwiseOrLiteral_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 0x0F | 0xF0; } }");
        AssertConversion(result, "return 0x0F | 0xF0;");
    }

    [Fact]
    public void DivisionOfLiterals_PassesThrough()
    {
        var result = Convert("class C { public int M() { return 100 / 4; } }");
        AssertConversion(result, "return 100 / 4;");
    }

    [Fact]
    public void DoubleFieldInitializer_PassesThrough()
    {
        var result = Convert("class C { public double Rate = 0.05; }");
        AssertConversion(result, "public double Rate = 0.05;");
    }

    [Fact]
    public void LongFieldInitializer_PassesThrough()
    {
        var result = Convert("class C { public long Big = 1000000L; }");
        AssertConversion(result, "public long Big = 1000000L;");
    }

    [Fact]
    public void NestedArithmeticLiterals_PassesThrough()
    {
        var result = Convert("class C { public int M() { return (2 + 3) * 4; } }");
        AssertConversion(result, "return (2 + 3) * 4;");
    }
}
