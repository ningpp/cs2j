using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# Math.Round conversion to Java.
///
/// Problem: C# Math.Round has multi-argument overloads; Java Math.round() only accepts 1 argument.
/// Direct mapping causes a Java compile error:
///   Math.Round(x, 2) → Math.round(x, 2)  ❌  "no suitable method found for round(double,int)"
///
/// Expected correct Java conversions:
///   Math.Round(x)       → (double)Math.round(x)                                              ✓
///   Math.Round(x, 2)    → BigDecimal.valueOf(x).setScale(2, RoundingMode.HALF_UP).doubleValue() ✓
///   Math.Round(x, n)    → BigDecimal.valueOf(x).setScale(n, RoundingMode.HALF_UP).doubleValue() ✓
/// </summary>
public class MathRoundConversionTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java17,
            }
        });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Test 1: Math.Round(double, int) with a literal digit count ────────────
    // C#:   double result = Math.Round(value, 2);
    // Java: double result = BigDecimal.valueOf(value).setScale(2, RoundingMode.HALF_UP).doubleValue();
    [Fact]
    public void MathRound_TwoArgs_LiteralDigits_EmitsBigDecimalSetScale()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double RoundValue(double value) {
                        return Math.Round(value, 2);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT generate the illegal two-argument Math.round call
        Assert.DoesNotContain("Math.round(value, 2)", java);
        Assert.DoesNotContain("Math.round(value, ", java);

        // Must generate BigDecimal setScale form
        Assert.Contains("BigDecimal.valueOf(value)", java);
        Assert.Contains("setScale(2, RoundingMode.HALF_UP)", java);
        Assert.Contains("doubleValue()", java);

        // Must include the required imports
        Assert.Contains("java.math.BigDecimal", java);
        Assert.Contains("java.math.RoundingMode", java);
    }

    // ── Test 2: Math.Round(double, int) with a variable digit count ───────────
    // C#:   double result = Math.Round(x, n);
    // Java: double result = BigDecimal.valueOf(x).setScale(n, RoundingMode.HALF_UP).doubleValue();
    [Fact]
    public void MathRound_TwoArgs_VariableDigits_EmitsBigDecimalSetScale()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double RoundValue(double x, int n) {
                        return Math.Round(x, n);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT generate the illegal two-argument Math.round call
        Assert.DoesNotContain("Math.round(x, n)", java);

        // Must generate BigDecimal setScale form
        Assert.Contains("BigDecimal.valueOf(x)", java);
        Assert.Contains("setScale(n, RoundingMode.HALF_UP)", java);
        Assert.Contains("doubleValue()", java);
    }

    // ── Test 3: Math.Round(double) single argument ────────────────────────────
    // C#:   double result = Math.Round(value);
    // Java: double result = (double)Math.round(value);
    // Note: Java Math.round(double) returns long; cast back to double.
    [Fact]
    public void MathRound_OneArg_EmitsDoubleCastForm()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double RoundValue(double value) {
                        return Math.Round(value);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must use single-arg Math.round with double cast
        Assert.Contains("(double)Math.round(value)", java);
    }

    // ── Test 4: Math.Round in a more complex expression context ───────────────
    // C#:   double price = Math.Round(rawPrice * 1.1, 2);
    // Java: double price = BigDecimal.valueOf(rawPrice * 1.1).setScale(2, RoundingMode.HALF_UP).doubleValue();
    [Fact]
    public void MathRound_TwoArgs_ComplexExpression_EmitsBigDecimalSetScale()
    {
        const string code = """
            using System;
            namespace Test {
                public class PriceCalculator {
                    public double CalcPrice(double rawPrice) {
                        double price = Math.Round(rawPrice * 1.1, 2);
                        return price;
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("setScale(2, RoundingMode.HALF_UP)", java);
        Assert.Contains("doubleValue()", java);
        Assert.DoesNotContain("Math.round(rawPrice", java);
    }

    // ── Test 5: Regression — Math.Floor and Math.Ceiling are not affected ─────
    // Math.Floor(x) → Math.floor(x)   (must remain unchanged)
    // Math.Ceiling(x) → Math.ceil(x)  (must remain unchanged)
    [Fact]
    public void MathFloor_And_Ceiling_Unaffected()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double FloorValue(double x) {
                        return Math.Floor(x);
                    }
                    public double CeilValue(double x) {
                        return Math.Ceiling(x);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("Math.floor(x)", java);
        Assert.Contains("Math.ceil(x)", java);
    }
}
