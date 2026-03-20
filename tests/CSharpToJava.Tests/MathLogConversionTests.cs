using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# Math.Log two-argument overload conversion to Java.
///
/// Problem: C# Math.Log(double a, double newBase) computes log base newBase of a.
/// Java's Math.log() only accepts 1 argument (natural logarithm).
/// Direct mapping causes a Java compile error:
///   Math.Log(a, newBase) → Math.log(a, newBase)  ❌  "no suitable method found for log(double,double)"
///
/// Fix: use the change-of-base formula →  Math.log(a) / Math.log(newBase)  ✓
///
/// Expected correct Java conversions:
///   Math.Log(x)           → Math.log(x)                            ✓  (1-arg, unchanged)
///   Math.Log(a, newBase)  → Math.log(a) / Math.log(newBase)        ✓  (2-arg, change-of-base)
///   Math.Log(a, 2.0)      → Math.log(a) / Math.log(2.0)            ✓  (2-arg, literal base)
/// </summary>
public class MathLogConversionTests
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

    // ── Test 1: Math.Log(double, double) with variable base ───────────────────
    // C#:   double result = Math.Log(a, newBase);
    // Java: double result = Math.log(a) / Math.log(newBase);
    [Fact]
    public void MathLog_TwoArgs_VariableBase_EmitsChangeOfBaseFormula()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double ComputeLog(double a, double newBase) {
                        return Math.Log(a, newBase);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT generate the illegal two-argument Math.log call
        Assert.DoesNotContain("Math.log(a, newBase)", java);
        Assert.DoesNotContain("Math.log(a, ", java);

        // Must generate the change-of-base formula
        Assert.Contains("Math.log(a) / Math.log(newBase)", java);
    }

    // ── Test 2: Math.Log(double, double) with a literal base ──────────────────
    // C#:   double result = Math.Log(x, 2.0);
    // Java: double result = Math.log(x) / Math.log(2.0);
    [Fact]
    public void MathLog_TwoArgs_LiteralBase_EmitsChangeOfBaseFormula()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double Log2(double x) {
                        return Math.Log(x, 2.0);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT generate the illegal two-argument Math.log call
        Assert.DoesNotContain("Math.log(x, 2.0)", java);

        // Must generate the change-of-base formula
        Assert.Contains("Math.log(x) / Math.log(2.0)", java);
    }

    // ── Test 3: Math.Log(double) single argument is not affected ──────────────
    // C#:   double result = Math.Log(x);
    // Java: double result = Math.log(x);
    [Fact]
    public void MathLog_OneArg_EmitsDirectMathLog()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double NaturalLog(double x) {
                        return Math.Log(x);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Single-arg overload must map directly to Math.log(x)
        Assert.Contains("Math.log(x)", java);

        // Must NOT contain the division form (that's only for 2-arg)
        Assert.DoesNotContain("Math.log(x) / Math.log(", java);
    }

    // ── Test 4: Regression — Math.Log10 is not affected ──────────────────────
    // C#:   double result = Math.Log10(x);
    // Java: double result = Math.log10(x);
    [Fact]
    public void MathLog10_OneArg_Unaffected()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double Log10(double x) {
                        return Math.Log10(x);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("Math.log10(x)", java);
        Assert.DoesNotContain("Math.log(x) /", java);
    }

    // ── Test 5: Math.Log in a complex expression context ─────────────────────
    // C#:   double bits = Math.Log(count, 2.0) + Math.Log(total, 10.0);
    // Java: double bits = Math.log(count) / Math.log(2.0) + Math.log(total) / Math.log(10.0);
    [Fact]
    public void MathLog_TwoArgs_InComplexExpression_EmitsChangeOfBaseFormula()
    {
        const string code = """
            using System;
            namespace Test {
                public class Calculator {
                    public double Entropy(double count, double total) {
                        return Math.Log(count, 2.0) + Math.Log(total, 10.0);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("Math.log(count) / Math.log(2.0)", java);
        Assert.Contains("Math.log(total) / Math.log(10.0)", java);
    }
}
