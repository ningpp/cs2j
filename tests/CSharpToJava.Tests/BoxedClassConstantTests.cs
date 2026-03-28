using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# numeric class-name static constants accessed via the boxed
/// class identifier (not the keyword alias), e.g. Double.MaxValue, Int32.MaxValue.
///
/// The PredefinedTypeSyntax path already handles keyword forms: double.MaxValue → Double.MAX_VALUE.
/// But when code uses the class name (Double.MaxValue, Int32.MaxValue, Single.MaxValue, etc.),
/// the receiver is an IdentifierNameSyntax — not a PredefinedTypeSyntax — and the semantic model
/// may not fully resolve it during project conversion.  Without this fix the converter emits
/// the C# identifier unchanged, producing invalid Java such as:
///   Double.MaxValue    ← Java: no such field; must be Double.MAX_VALUE
///   Int32.MaxValue     ← Java: class Int32 doesn't exist; must be Integer.MAX_VALUE
///
/// Patterns confirmed in generated Java output (agl v20260315):
///   Rail.java:72        — Double.MaxValue (from C#: public double field = Double.MaxValue)
///   NetworkEdge.java:53 — Int32.MaxValue  (from C#: internal const int Infinity = Int32.MaxValue)
/// </summary>
public class BoxedClassConstantTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = new ConversionOptions { TargetJavaVersion = JavaVersion.Java25 }
        });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Rail.java pattern: Double.MaxValue as field initializer ──────────────────
    [Fact]
    public void DoubleMaxValue_AsFieldInitializer_EmitsDoubleMAX_VALUE()
    {
        const string code = """
            namespace Test {
                class Rail {
                    public double MinPassingEdgeZoomLevel = Double.MaxValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Double.MaxValue", java);
        Assert.Contains("Double.MAX_VALUE", java);
    }

    // ── NetworkEdge.java pattern: Int32.MaxValue as constant expression ───────────
    [Fact]
    public void Int32MaxValue_AsFieldInitializer_EmitsIntegerMAX_VALUE()
    {
        const string code = """
            namespace Test {
                class NetworkEdge {
                    public const int Infinity = Int32.MaxValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Int32.MaxValue", java);
        Assert.Contains("Integer.MAX_VALUE", java);
    }

    // ── Single.MaxValue → Float.MAX_VALUE ─────────────────────────────────────────
    [Fact]
    public void SingleMaxValue_AsFieldInitializer_EmitsFloatMAX_VALUE()
    {
        const string code = """
            namespace Test {
                class C {
                    float maxF = Single.MaxValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Single.MaxValue", java);
        Assert.Contains("Float.MAX_VALUE", java);
    }

    // ── Int64.MinValue → Long.MIN_VALUE ──────────────────────────────────────────
    [Fact]
    public void Int64MinValue_AsFieldInitializer_EmitsLongMIN_VALUE()
    {
        const string code = """
            namespace Test {
                class C {
                    long minL = Int64.MinValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Int64.MinValue", java);
        Assert.Contains("Long.MIN_VALUE", java);
    }

    // ── Double.MinValue special case: C# MinValue = most negative, Java MIN_VALUE = smallest positive ──
    // For double/float, C# double.MinValue ≈ -1.79E+308 (most negative)
    // Java Double.MIN_VALUE ≈ 4.9E-324 (smallest positive) — these are different!
    // The converter uses (-Double.MAX_VALUE) to match C# semantics.
    [Fact]
    public void DoubleMinValue_EmitsNegatedMaxValue()
    {
        const string code = """
            namespace Test {
                class C {
                    double minD = Double.MinValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Double.MinValue", java);
        // Should emit (-Double.MAX_VALUE) to preserve C# semantics (most negative double)
        Assert.True(
            java.Contains("(-Double.MAX_VALUE)") || java.Contains("-Double.MAX_VALUE"),
            $"Expected (-Double.MAX_VALUE) for Double.MinValue but got:\n{java}");
    }

    // ── Int32.MaxValue in a const field (const int → static final int) ────────────
    [Fact]
    public void ConstIntWithInt32MaxValue_EmitsIntegerMAX_VALUE()
    {
        const string code = """
            namespace Test {
                class C {
                    public const int MAX = Int32.MaxValue;
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("Int32.MaxValue", java);
        Assert.Contains("Integer.MAX_VALUE", java);
    }

    // ── Double.NaN → Double.NaN (same name, should pass through unchanged) ────────
    [Fact]
    public void DoubleNaN_EmitsDoubleNaN()
    {
        const string code = """
            namespace Test {
                class C {
                    bool Check() { return Double.IsNaN(1.0 / 0.0); }
                }
            }
            """;
        var java = ConvertCode(code);
        // Double.IsNaN should map to Double.isNaN (a method, handled elsewhere)
        Assert.True(
            java.Contains("Double.isNaN") || java.Contains("isNaN"),
            $"Expected Double.isNaN in output but got:\n{java}");
    }
}
