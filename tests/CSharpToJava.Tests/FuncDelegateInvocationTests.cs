using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Func&lt;...&gt; type mapping and delegate-invocation rewriting.
///
/// Bugs fixed:
///   1. Func&lt;T,R&gt; was emitting Vavr's Function1&lt;&gt; instead of java.util.function.Function&lt;&gt;.
///   2. Bare delegate invocations (sequence(m)) were not rewritten to sequence.apply(m).
/// </summary>
public class FuncDelegateInvocationTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug 1a ─────────────────────────────────────────────────────────────────
    // Func<int, double> must map to java.util.function.Function<Integer, Double>,
    // NOT to io.vavr.Function1<Integer, Double>.

    [Fact]
    public void Func2_MapsToStandardJavaFunction()
    {
        const string code = """
            class UnimodalSequence
            {
                System.Func<int, double> sequence;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Function<Integer, Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function1", result.GeneratedCode);
        Assert.DoesNotContain("io.vavr", result.GeneratedCode);
    }

    // ── Bug 1b ─────────────────────────────────────────────────────────────────
    // Func<double> (0-input, 1-output) must map to java.util.function.Supplier<Double>.

    [Fact]
    public void Func1_MapsToSupplier()
    {
        const string code = """
            class Example
            {
                System.Func<double> factory;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Supplier<Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function0", result.GeneratedCode);
    }

    // ── Bug 1c ─────────────────────────────────────────────────────────────────
    // Func<int, int, double> (2-input) must map to java.util.function.BiFunction<Integer, Integer, Double>.

    [Fact]
    public void Func3_MapsToBiFunction()
    {
        const string code = """
            class Example
            {
                System.Func<int, int, double> combine;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("BiFunction<Integer, Integer, Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function2", result.GeneratedCode);
    }

    // ── Bug 2a ─────────────────────────────────────────────────────────────────
    // Invoking a Func<int, double> field as sequence(m) must emit sequence.apply(m).

    [Fact]
    public void Func2_BareDelegateInvocation_EmitsApply()
    {
        const string code = """
            class UnimodalSequence
            {
                System.Func<int, double> sequence;

                public double FindSequenceScore(int m)
                {
                    return sequence(m);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("sequence.apply(m)", result.GeneratedCode);
        // Must NOT appear as a bare call
        Assert.DoesNotContain("return sequence(m)", result.GeneratedCode);
    }

    // ── Bug 2b ─────────────────────────────────────────────────────────────────
    // Invoking a Func<double> (Supplier) field as factory() must emit factory.get().

    [Fact]
    public void Func1_BareDelegateInvocation_EmitsGet()
    {
        const string code = """
            class Example
            {
                System.Func<double> factory;

                public double GetValue()
                {
                    return factory();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("factory.get()", result.GeneratedCode);
        Assert.DoesNotContain("return factory()", result.GeneratedCode);
    }
}
