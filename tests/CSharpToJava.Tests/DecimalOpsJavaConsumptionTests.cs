using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Java-half consumption of the C34 decimal-ops normalization. The preprocessor
/// rewrites decimal arithmetic into explicit <c>(decimal)</c> operand promotions
/// and <c>decimal.Add/Subtract/.../Compare/Negate</c> static calls; the main
/// converter must then lower those into the compat Decimal surface
/// (<c>Decimal.valueOf(...)</c> factories and lowercase static methods) and close
/// the file with the <c>io.github.ningpp.compat.Decimal</c> import. Java has no
/// primitive-to-reference cast, so <c>(Decimal)(i)</c> would be illegal output.
/// </summary>
public class DecimalOpsJavaConsumptionTests
{
    private const string DecimalImport = "import io.github.ningpp.compat.Decimal;";

    [Fact]
    public void CastIntToDecimal_BecomesValueOf_NotPrimitiveCast()
    {
        var result = Convert("""
class Sample
{
    decimal Promote(int i)
    {
        return (decimal)i;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(i)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DecimalImport, result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastLongToDecimal_BecomesValueOf()
    {
        var result = Convert("""
class Sample
{
    decimal Promote(long l)
    {
        return (decimal)l;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(l)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastDoubleToDecimal_BecomesValueOf()
    {
        var result = Convert("""
class Sample
{
    decimal Promote(double d)
    {
        return (decimal)d;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(d)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastIntLiteralToDecimal_BecomesValueOf()
    {
        var result = Convert("""
class Sample
{
    decimal Get()
    {
        return (decimal)1;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(1)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastUIntToDecimal_KeepsUnsignedMagnitude()
    {
        var result = Convert("""
class Sample
{
    decimal Promote(uint u)
    {
        return (decimal)u;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("UnsignedMath.toDecimalUInt32(u)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.UnsignedMath;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastULongToDecimal_KeepsUnsignedMagnitude()
    {
        var result = Convert("""
class Sample
{
    decimal Promote(ulong l)
    {
        return (decimal)l;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("UnsignedMath.toDecimalUInt64(l)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.UnsignedMath;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastEnumToDecimal_BecomesValueOfOrdinal()
    {
        var result = Convert("""
enum Color { Red, Green, Blue }
class Sample
{
    decimal Promote(Color c)
    {
        return (decimal)c;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(c.ordinal())", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityDecimalCast_IsDropped()
    {
        var result = Convert("""
class Sample
{
    decimal PassThrough(decimal d)
    {
        return (decimal)d;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return d;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Decimal.valueOf", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void C34CanonicalForm_DecimalAddWithPromotedInt_ClosesAsJava()
    {
        var result = Convert("""
class Sample
{
    decimal AddPromoted(decimal a, int i)
    {
        return decimal.Add(a, (decimal)i);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.add(a, Decimal.valueOf(i))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DecimalImport, result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Decimal)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Add", "add")]
    [InlineData("Subtract", "subtract")]
    [InlineData("Multiply", "multiply")]
    [InlineData("Divide", "divide")]
    [InlineData("Remainder", "remainder")]
    [InlineData("Negate", "negate")]
    [InlineData("Compare", "compare")]
    public void DecimalStaticCalls_LowerToCompatDecimal(string csMethod, string javaMethod)
    {
        var returnType = csMethod == "Compare" ? "int" : "decimal";
        var arguments = csMethod == "Negate" ? "a" : "a, b";
        var result = Convert($$"""
class Sample
{
    static {{returnType}} Run(decimal a, decimal b)
    {
        return decimal.{{csMethod}}({{arguments}});
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains($"Decimal.{javaMethod}({arguments})", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DecimalImport, result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalParse_EmitsCompatParseWithImport()
    {
        var result = Convert("""
class Sample
{
    decimal Parse(string text)
    {
        return decimal.Parse(text);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.parse(text)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DecimalImport, result.GeneratedCode, StringComparison.Ordinal);
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
