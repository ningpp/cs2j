using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DecimalMappingTests
{
    [Fact]
    public void DecimalLocalDeclaration_UsesCompatDecimal()
    {
        var result = Convert("""
class Sample
{
    decimal GetValue()
    {
        decimal value = 1.23m;
        return value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.Decimal;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal value = Decimal.parse(\"1.23\");", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("BigDecimal", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double getValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemDecimalParse_UsesCompatDecimal()
    {
        var result = Convert("""
using System;

class Sample
{
    Decimal GetValue()
    {
        return Decimal.Parse("42.5");
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.Decimal;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Decimal.parse(\"42.5\");", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalTryParse_UsesCompatDecimal()
    {
        var result = Convert("""
class Sample
{
    bool Try(string text)
    {
        decimal value;
        return decimal.TryParse(text, out value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal value", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal.tryParse(text", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MathHelper.tryParseDouble", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalArithmeticAndComparison_UseCompatMethods()
    {
        var result = Convert("""
class Sample
{
    decimal Calculate(decimal a, decimal b)
    {
        decimal sum = a + b;
        decimal diff = a - b;
        decimal product = a * b;
        decimal quotient = a / b;
        decimal remainder = a % b;
        return sum > diff ? product : quotient + remainder;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal sum = a.add(b);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal diff = a.subtract(b);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal product = a.multiply(b);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal quotient = a.divide(b);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal remainder = a.remainder(b);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("sum.compareTo(diff) > 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("quotient.add(remainder)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalUnaryMinus_UsesCompatNegate()
    {
        var result = Convert("""
class Sample
{
    decimal Negate(decimal value)
    {
        return -value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return value.negate();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericToDecimalConversions_WrapWithCompatFactory()
    {
        var result = Convert("""
class Sample
{
    decimal FromValues(int i, long l, double x)
    {
        decimal fromIntLiteral = 1;
        decimal fromIntVariable = i;
        decimal fromLongVariable = l;
        decimal fromDoubleCast = (decimal)x;
        return fromIntLiteral + fromIntVariable + fromLongVariable + fromDoubleCast;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal fromIntLiteral = Decimal.valueOf(1);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal fromIntVariable = Decimal.valueOf(i);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal fromLongVariable = Decimal.valueOf(l);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal fromDoubleCast = Decimal.valueOf(x);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalToNumericCasts_UseCompatConversions()
    {
        var result = Convert("""
class Sample
{
    int ToInt(decimal value) => (int)value;
    long ToLong(decimal value) => (long)value;
    double ToDouble(decimal value) => (double)value;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return value.intValue();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return value.longValue();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return value.doubleValue();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(int)(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(long)(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(double)(value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalMixedArithmeticAndComparison_WrapNumericOperands()
    {
        var result = Convert("""
class Sample
{
    bool IsLarge(decimal value, int delta)
    {
        decimal sum = value + delta;
        decimal product = 2 * value;
        return sum >= product && value == 1;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal sum = value.add(Decimal.valueOf(delta));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal product = Decimal.valueOf(2).multiply(value);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("sum.compareTo(product) >= 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value.compareTo(Decimal.valueOf(1)) == 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalCompoundAssignment_UsesCompatMethods()
    {
        var result = Convert("""
class Sample
{
    decimal Update(decimal value, decimal other, int count)
    {
        value += other;
        value -= 1;
        value *= count;
        value /= 2;
        value %= other;
        return value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value = value.add(other);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = value.subtract(Decimal.valueOf(1));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = value.multiply(Decimal.valueOf(count));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = value.divide(Decimal.valueOf(2));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = value.remainder(other);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value +=", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value -=", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalIncrementAndDecrement_UseCompatMethods()
    {
        var result = Convert("""
class Sample
{
    decimal Update(decimal value)
    {
        value++;
        --value;
        return value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value = value.add(Decimal.ONE);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = value.subtract(Decimal.ONE);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value++", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("--value", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalDefaults_UseZero()
    {
        var result = Convert("""
class Sample
{
    decimal field;

    decimal GetDefault()
    {
        decimal explicitDefault = default(decimal);
        decimal targetDefault = default;
        decimal constructed = new decimal();
        return field + explicitDefault + targetDefault + constructed;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal field = Decimal.ZERO;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal explicitDefault = Decimal.ZERO;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal targetDefault = Decimal.ZERO;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal constructed = Decimal.ZERO;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalConstants_MapToCompatConstants()
    {
        var result = Convert("""
class Sample
{
    decimal Get()
    {
        return decimal.MaxValue - decimal.MinValue + decimal.One + decimal.Zero + decimal.MinusOne;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal.MIN_VALUE", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal.ONE", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal.ZERO", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Decimal.MINUS_ONE", result.GeneratedCode, StringComparison.Ordinal);
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
