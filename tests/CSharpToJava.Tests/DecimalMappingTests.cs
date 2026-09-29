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
